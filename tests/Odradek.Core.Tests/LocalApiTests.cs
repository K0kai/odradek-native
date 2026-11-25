using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Api;
using Odradek.Core.Candidate;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Engines;
using Odradek.LocalApi;

namespace Odradek.Core.Tests;

/// <summary>O servidor real (Kestrel) sobre um banco temporário, com um cliente HTTP.</summary>
public sealed class ApiHarness : IDisposable
{
    public const string QpidToken = "qpid-test-token";
    public TempDb Temp { get; } = new();
    public JobCollector Collector { get; }
    public FakeAiHandler Ai { get; } = new();
    public RouteHandler LinkedIn { get; } = new();
    /// <summary>Fila do LinkedIn do servidor: arquivo temporário e pausas que nunca terminam, para o trabalho em segundo plano não correr atrás do teste.</summary>
    public LinkedInQueue Queue { get; }
    public string AiKey { get; set; } = "K";
    public HttpClient Client { get; }
    private readonly LocalServer _server;

    private ApiHarness(string qpidToken, Func<CancellationToken, Task<bool>>? confirmPairing, TimeSpan? pairingTimeout, Func<string?>? extensionVersion)
    {
        Collector = new JobCollector(Temp.Db, new Dictionary<string, JobSources.Provider>());
        var ai = new AiClient(new HttpClient(Ai), null, null, new AiClientOptions { FallbackModels = [], RetryDelays = [], Pause = (_, _) => Task.CompletedTask });
        var assistant = new Assistant(Temp.Db, ai, _ => AiKey);
        Queue = new LinkedInQueue(Temp.Db, () => new HttpClient(LinkedIn), Path.Combine(Path.GetTempPath(), "odradek-queue-" + Guid.NewGuid().ToString("N") + ".json"),
            delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var services = new ApiServices(Temp.Db, () => Collector, () => assistant, () => qpidToken, () => new HttpClient(LinkedIn), Queue)
        {
            ConfirmPairing = confirmPairing, PairingTimeout = pairingTimeout ?? TimeSpan.FromSeconds(30), ExtensionVersion = extensionVersion,
        };
        _server = new LocalServer(services, port);
        _server.StartAsync().GetAwaiter().GetResult();
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
    }

    public static ApiHarness Start(string qpidToken = QpidToken, Func<CancellationToken, Task<bool>>? confirmPairing = null,
        TimeSpan? pairingTimeout = null, Func<string?>? extensionVersion = null) => new(qpidToken, confirmPairing, pairingTimeout, extensionVersion);

    public HttpRequestMessage Request(string method, string url, string? token = QpidToken, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> Send(string method, string url, string? token = QpidToken, HttpContent? content = null)
    {
        var response = await Client.SendAsync(Request(method, url, token, content));
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    public async Task<(HttpStatusCode Status, JsonNode? Json)> Json(string method, string url, string? token = QpidToken, string? body = null)
    {
        var (status, text, _) = await Send(method, url, token, body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"));
        return (status, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    public void Dispose()
    {
        Queue.Dispose();
        Collector.Stop();
        Collector.WhenIdle().Wait(TimeSpan.FromSeconds(10));
        Client.Dispose();
        _server.StopAsync().GetAwaiter().GetResult();
        Temp.Dispose();
    }
}

public class QpidContractTests
{
    private static readonly JsonObject Reference = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "qpid_cases.json")))!;

    private static void ApplySeed(TempDb t)
    {
        using var connection = t.Db.Open();
        foreach (var entry in Reference["seed"]!.AsArray())
        {
            var sql = entry![0]!.GetValue<string>();
            using var command = connection.CreateCommand();
            var index = 0;
            command.CommandText = System.Text.RegularExpressions.Regex.Replace(sql, @"\?", _ => $"@p{index++}");
            var values = entry[1]!.AsArray();
            for (var i = 0; i < values.Count; i++)
            {
                object? value = values[i] switch
                {
                    null => DBNull.Value,
                    JsonObject o => Convert.FromHexString(o["hex"]!.GetValue<string>()),
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    var other => long.Parse(other.ToJsonString()),
                };
                command.Parameters.AddWithValue($"@p{i}", value);
            }
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task The_application_contract_answers_exactly_like_the_python_app()
    {
        using var api = ApiHarness.Start();
        ApplySeed(api.Temp);
        var number = 0;
        foreach (var item in Reference["cases"]!.AsArray().OfType<JsonObject>())
        {
            number++;
            var method = item["method"]!.GetValue<string>();
            var url = item["url"]!.GetValue<string>();
            var body = item["body"]?.ToJsonString();
            var (status, text, response) = await api.Send(method, url, content: body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"));
            var label = $"#{number} {method} {url}";
            Assert.True((int)item["status"]!.GetValue<int>() == (int)status, $"{label}: status {status}, corpo {text}");
            var expected = item["response"]!;
            if (response.Content.Headers.ContentType?.MediaType == "application/pdf")
            {
                Assert.Equal(expected["pdf_sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(await response.Content.ReadAsByteArrayAsync())));
                Assert.Equal("application/pdf", expected["media_type"]!.GetValue<string>());
                Assert.Equal("attachment; filename=\"curriculo.pdf\"", response.Content.Headers.ContentDisposition?.ToString());
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                continue;
            }
            var actual = JsonNode.Parse(text)!;
            Assert.True(expected.ToJsonString() == actual.ToJsonString(), $"{label}\n  esperado: {expected.ToJsonString()}\n  recebido: {actual.ToJsonString()}");
        }
        foreach (var job in Reference["final_jobs"]!.AsArray().OfType<JsonObject>())
            Assert.Equal(job["status"]!.GetValue<string>(), api.Temp.Scalar<string>("SELECT status FROM jobs WHERE id=@id", new { id = job["id"]!.GetValue<long>() }));
    }

    [Fact]
    public void Resume_excerpts_match_the_python_ones_including_long_lines_and_emoji()
    {
        foreach (var item in Reference["excerpts"]!.AsArray().OfType<JsonObject>())
        {
            var actual = ResumeExcerpts.Of(item["text"]!.GetValue<string>(), item["profile"] as JsonObject, item["max_chars"]!.GetValue<int>(), item["language"]!.GetValue<string>());
            var expected = item["result"]!.AsArray().OfType<JsonObject>().Select(e => (e["section"]!.GetValue<string>(), e["label"]!.GetValue<string>(), e["text"]!.GetValue<string>())).ToList();
            Assert.True(expected.SequenceEqual(actual.Select(e => (e.Section, e.Label, e.Text))), $"{item["name"]}: {expected.Count} esperados, {actual.Count} recebidos");
        }
    }

    [Theory]
    [InlineData("https://acme.test/apply/ok?x=1#frag", "https", "acme.test", "/apply/ok", "", "")]
    [InlineData("https://user:pw@Acme.test:8443/p", "https", "acme.test", "/p", "user", "pw")]
    [InlineData("  \thttps://a.test", "https", "a.test", "", "", "")]
    [InlineData("http://a.test/x", "http", "a.test", "/x", "", "")]
    [InlineData("a.test/x", "", "", "a.test/x", "", "")]
    [InlineData("https://@a.test/x", "https", "a.test", "/x", "", "")]
    public void Result_urls_are_split_like_python_urlsplit(string raw, string scheme, string host, string path, string user, string password)
    {
        var parts = QpidApi.SplitUrl(raw)!.Value;
        Assert.Equal((scheme, host, path, user, password), (parts.Scheme, parts.Host, parts.Path, parts.User, parts.Password));
    }

    [Fact]
    public void An_unbalanced_ipv6_url_is_invalid() => Assert.Null(QpidApi.SplitUrl("https://[::1/x"));
}
