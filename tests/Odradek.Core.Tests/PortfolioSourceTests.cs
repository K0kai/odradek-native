using System.Text.Json.Nodes;
using Odradek.Core.Candidate;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>Paridade com <c>candidate/portfolio_source.py</c> (payload sintético gerado e processado pelo Python).</summary>
public class PortfolioSourceTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "portfolio_cases.json")))!;

    [Fact]
    public void Validation_keeps_only_well_formed_records_like_python()
    {
        var valid = PortfolioSource.ValidatePayload(Cases["payload"]!.DeepClone());
        Assert.Null(JsonCompare.Diff(Cases["valid"], valid));
    }

    [Fact]
    public void Validation_errors_have_the_same_messages()
    {
        var payloads = new Dictionary<string, JsonNode?>
        {
            ["not_object"] = JsonNode.Parse("[1]"), ["no_schema"] = JsonNode.Parse("""{"version":"v","records":[1]}"""),
            ["schema_zero"] = JsonNode.Parse("""{"schema":0,"version":"v","records":[1]}"""), ["schema_bool"] = JsonNode.Parse("""{"schema":true,"version":"v","records":[1]}"""),
            ["schema_float"] = JsonNode.Parse("""{"schema":1.5,"version":"v","records":[1]}"""), ["no_version"] = JsonNode.Parse("""{"schema":1,"records":[1]}"""),
            ["empty_version"] = JsonNode.Parse("""{"schema":1,"version":"","records":[1]}"""), ["no_records"] = JsonNode.Parse("""{"schema":1,"version":"v","records":[]}"""),
            ["no_facts"] = JsonNode.Parse("""{"schema":1,"version":"v","records":["x",{"id":"x","kind":"p","language":"pt","text":"t"}]}"""),
        };
        foreach (var item in Cases["validation_errors"]!.AsArray().OfType<JsonObject>())
        {
            var name = item["name"]!.GetValue<string>();
            var ex = Assert.Throws<PortfolioException>(() => PortfolioSource.ValidatePayload(payloads[name]));
            Assert.Equal(item["message"]!.GetValue<string>(), ex.Message);
        }
    }

    [Theory]
    [InlineData("pt", "profile_pt")]
    [InlineData("en", "profile_en")]
    [InlineData("xx", "profile_xx")]
    public void Profile_matches_python(string language, string key)
    {
        var profile = PortfolioSource.BuildProfile((JsonObject)Cases["valid"]!.DeepClone(), language);
        Assert.Null(JsonCompare.Diff(Cases[key], profile));
    }

    [Fact]
    public void Text_matches_python() =>
        Assert.Equal(Cases["text"]!.GetValue<string>(), PortfolioSource.PortfolioText((JsonObject)Cases["valid"]!.DeepClone()));

    [Fact]
    public async Task Sync_stores_the_snapshot_and_skips_when_the_version_has_not_changed()
    {
        using var temp = new TempDb();
        var settings = new SettingsStore(temp.Db);
        var calls = 0;
        Task<JsonObject> Fetch(string url, CancellationToken ct) { calls++; return Task.FromResult(PortfolioSource.ValidatePayload(Cases["payload"]!.DeepClone())); }

        Assert.Equal("disabled", (await PortfolioSource.SyncAsync(temp.Db, " ", Fetch)).Status);
        var first = await PortfolioSource.SyncAsync(temp.Db, "https://example.test/ai/profile.json", Fetch, nowIso: "2026-11-23T10:00:00+00:00");
        Assert.Equal("updated", first.Status);
        Assert.Equal("Portfólio sincronizado: 2 cargo(s), 2 projeto(s), 13 skill(s).", first.Message);
        Assert.Equal("2026-11-23T10:00:00Z+b1", settings.Get("portfolio_version"));
        Assert.Equal("2026-11-23T10:00:00+00:00", settings.Get("portfolio_synced_at"));

        var loaded = PortfolioSource.Load(temp.Db, "pt")!.Value;
        Assert.Null(JsonCompare.Diff(Cases["profile_pt"], loaded.Profile));
        Assert.Equal(Cases["text"]!.GetValue<string>(), loaded.Text);
        Assert.Empty((JsonArray)PortfolioSource.Load(temp.Db, "en")!.Value.Profile["projects"]!);

        Assert.Equal("unchanged", (await PortfolioSource.SyncAsync(temp.Db, "https://example.test/ai/profile.json", Fetch)).Status);
        Assert.Equal("updated", (await PortfolioSource.SyncAsync(temp.Db, "https://example.test/ai/profile.json", Fetch, force: true)).Status);

        var status = PortfolioSource.Status(temp.Db);
        Assert.Equal(2, status["counts"]!["roles"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_failed_sync_keeps_the_last_good_snapshot_and_records_the_error()
    {
        using var temp = new TempDb();
        var settings = new SettingsStore(temp.Db);
        await PortfolioSource.SyncAsync(temp.Db, "https://example.test/p", (_, _) => Task.FromResult(PortfolioSource.ValidatePayload(Cases["payload"]!.DeepClone())));
        var good = settings.Get("portfolio_profile_json");

        var result = await PortfolioSource.SyncAsync(temp.Db, "https://example.test/p", (_, _) => throw new PortfolioException("Não foi possível buscar o portfólio: sem rede"));
        Assert.Equal("error", result.Status);
        Assert.Equal("Não foi possível buscar o portfólio: sem rede", settings.Get("portfolio_error"));
        Assert.Equal(good, settings.Get("portfolio_profile_json"));
        Assert.NotNull(PortfolioSource.Load(temp.Db));
    }

    [Fact]
    public async Task Fetch_rejects_bad_urls_without_touching_the_network()
    {
        using var http = new HttpClient(new FailingHandler());
        foreach (var url in new[] { "", "ftp://x.test/a", "not a url", "http://" })
            await Assert.ThrowsAsync<PortfolioException>(() => PortfolioSource.FetchAsync(http, url));
    }

    [Fact]
    public async Task Fetch_limits_the_body_and_validates_json()
    {
        using var big = new HttpClient(new StaticHandler(new string('x', PortfolioSource.MaxBodyBytes + 10)));
        Assert.Equal("Resposta do portfólio maior que 2 MB.", (await Assert.ThrowsAsync<PortfolioException>(() => PortfolioSource.FetchAsync(big, "https://example.test/p"))).Message);
        using var notJson = new HttpClient(new StaticHandler("<html>"));
        Assert.Equal("O portfólio não retornou JSON válido.", (await Assert.ThrowsAsync<PortfolioException>(() => PortfolioSource.FetchAsync(notJson, "https://example.test/p"))).Message);
        using var ok = new HttpClient(new StaticHandler(Cases["payload"]!.ToJsonString()));
        Assert.Null(JsonCompare.Diff(Cases["valid"], await PortfolioSource.FetchAsync(ok, "https://example.test/p")));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("rede não deveria ser usada");
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("job-scraper-portfolio-sync/1", request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
