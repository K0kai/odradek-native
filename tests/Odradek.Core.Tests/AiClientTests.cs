using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Odradek.Core.Ai;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>Responde às chamadas de IA com o que o teste enfileirar e guarda cada requisição (URL sem chave, corpo).</summary>
public sealed class FakeAiHandler : HttpMessageHandler
{
    public sealed record Seen(string Host, string Model, JsonObject Body, string? Bearer, string Url, string? GoogleKey);

    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    public List<Seen> Requests { get; } = [];

    public FakeAiHandler Reply(int status, string body) { _script.Enqueue(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }); return this; }
    public FakeAiHandler Throw(Exception exc) { _script.Enqueue(_ => throw exc); return this; }

    public FakeAiHandler Gemini(string text, int? tin = null, int? tout = null, string finish = "STOP")
    {
        var usage = tin is null ? "" : $",\"usageMetadata\":{{\"promptTokenCount\":{tin},\"candidatesTokenCount\":{tout}}}";
        var payload = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["finishReason"] = finish, ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) } }) };
        return Reply(200, payload.ToJsonString().TrimEnd('}') + usage + "}");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(request.Content!.ReadAsStringAsync(cancellationToken).Result) as JsonObject ?? [];
        var path = request.RequestUri!.AbsolutePath;
        var model = body["model"]?.ToString() ?? path.Split('/').Last().Split(':')[0];
        var googleKey = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null;
        Requests.Add(new Seen(request.RequestUri.Host, model, body, request.Headers.Authorization?.Parameter, request.RequestUri.ToString(), googleKey));
        if (_script.Count == 0) throw new InvalidOperationException("sem resposta enfileirada para " + request.RequestUri.Host + path);
        return Task.FromResult(_script.Dequeue()(request));
    }
}

public class AiClientTests
{
    private static readonly AiCall Call = new("gemini", "gemini-main", "SECRET-KEY-123");
    private static readonly JsonObject Schema = JsonNode.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}}}""")!.AsObject();
    private static (AiClient Client, List<TimeSpan> Pauses) Build(FakeAiHandler handler, UsageTracker? usage = null, Func<(string, IReadOnlyList<string>)>? th = null, IReadOnlyList<string>? fallback = null, int rounds = 3)
    {
        var pauses = new List<TimeSpan>();
        var options = new AiClientOptions
        {
            FallbackModels = fallback ?? ["gemini-backup"],
            RetryDelays = new[] { 2, 6, 15 }.Take(rounds).Select(s => TimeSpan.FromSeconds(s)).ToArray(),
            Pause = (span, _) => { pauses.Add(span); return Task.CompletedTask; },
        };
        return (new AiClient(new HttpClient(handler), usage, th, options), pauses);
    }

    [Fact]
    public async Task Text_returns_the_reply_skips_thought_parts_and_reports_the_model()
    {
        var handler = new FakeAiHandler().Reply(200, """{"candidates":[{"content":{"parts":[{"text":"pensando","thought":true},{"text":"Olá"},{"text":"mundo"}]}}]}""");
        var (client, _) = Build(handler);
        string? model = null;
        Assert.Equal("Olá\nmundo", await client.TextAsync("oi", Call, onModel: m => model = m));
        Assert.Equal("gemini-main", model);
        Assert.Equal("oi", handler.Requests[0].Body["contents"]![0]!["parts"]![0]!["text"]!.ToString());
        Assert.Equal(0.2, handler.Requests[0].Body["generationConfig"]!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task Overloaded_primary_falls_back_to_the_next_model_and_cools_the_failed_one_down()
    {
        var handler = new FakeAiHandler().Reply(503, """{"error":{"status":"UNAVAILABLE","message":"high demand"}}""").Gemini("{\"ok\":true}").Gemini("{\"ok\":true}");
        var (client, pauses) = Build(handler);
        string? used = null;
        Assert.True((await client.JsonAsync("p", Schema, Call, onModel: m => used = m))["ok"]!.GetValue<bool>());
        Assert.Equal("gemini-backup", used);
        Assert.Equal(["gemini-main", "gemini-backup"], handler.Requests.Select(r => r.Model));
        Assert.Empty(pauses);
        Assert.Equal(["gemini-backup", "gemini-main"], client.ModelChain("gemini", "gemini-main")); // quem falhou vai para o fim
        await client.JsonAsync("p", Schema, Call);
        Assert.Equal("gemini-backup", handler.Requests[2].Model);
    }

    [Fact]
    public async Task Transient_failures_repeat_the_whole_chain_with_backoff_then_give_up()
    {
        var handler = new FakeAiHandler();
        for (var i = 0; i < 8; i++) handler.Reply(503, """{"error":{"message":"busy"}}""");
        var (client, pauses) = Build(handler);
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.TextAsync("p", Call));
        Assert.Equal(3, pauses.Count);
        Assert.InRange(pauses[0].TotalSeconds, 1.5, 2.5);
        Assert.InRange(pauses[1].TotalSeconds, 4.5, 7.5);
        Assert.InRange(pauses[2].TotalSeconds, 11.25, 18.75);
        Assert.Equal(8, handler.Requests.Count); // 4 rodadas × (principal + 1 reserva)
        Assert.Equal(503, ex.HttpStatus);
        Assert.StartsWith("IA indisponível: nenhum modelo respondeu (gemini-main: HTTP 503; gemini-backup: HTTP 503)", ex.Message);
    }

    [Fact]
    public async Task Pure_quota_exhaustion_is_not_worth_waiting_for()
    {
        var handler = new FakeAiHandler().Reply(429, """{"error":{"message":"quota exceeded"}}""").Reply(429, """{"error":{"message":"quota exceeded"}}""");
        var (client, pauses) = Build(handler);
        await Assert.ThrowsAsync<AiUnavailableException>(() => client.TextAsync("p", Call));
        Assert.Empty(pauses);
    }

    [Fact]
    public async Task Bad_key_fails_immediately_without_trying_other_models_and_never_leaks_the_key()
    {
        var handler = new FakeAiHandler().Reply(401, """{"error":{"status":"UNAUTHENTICATED","message":"API key SECRET-KEY-123 not valid"}}""");
        var (client, _) = Build(handler);
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.TextAsync("p", Call));
        Assert.Single(handler.Requests);
        Assert.Equal(401, ex.HttpStatus);
        Assert.DoesNotContain("SECRET-KEY-123", ex.Message);
        Assert.Contains("[chave omitida]", ex.Message);
        Assert.Contains("UNAUTHENTICATED", ex.Message);
    }

    [Fact]
    public async Task Gemini_key_goes_in_the_header_and_never_in_the_url()
    {
        var handler = new FakeAiHandler().Gemini("ok");
        var (client, _) = Build(handler);
        await client.TextAsync("p", Call);
        Assert.Equal("SECRET-KEY-123", handler.Requests[0].GoogleKey);
        Assert.DoesNotContain("SECRET-KEY-123", handler.Requests[0].Url);
        Assert.Null(handler.Requests[0].Bearer);
    }

    [Fact]
    public async Task Missing_key_is_unavailable_without_any_http_call()
    {
        var handler = new FakeAiHandler();
        var (client, _) = Build(handler);
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.TextAsync("p", Call with { ApiKey = "" }));
        Assert.Equal("IA indisponivel: chave nao configurada.", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Retired_model_404_skips_to_the_next_with_a_long_cooldown()
    {
        var handler = new FakeAiHandler().Reply(404, """{"error":{"message":"no longer available"}}""").Gemini("ok");
        var clock = DateTime.UtcNow;
        var client = new AiClient(new HttpClient(handler), null, null, new AiClientOptions { FallbackModels = ["gemini-backup"], RetryDelays = [], UtcNow = () => clock });
        Assert.Equal("ok", await client.TextAsync("p", Call));
        clock += TimeSpan.FromMinutes(10); // 90 s já passaram, mas o 404 vale 6 h
        Assert.Equal(["gemini-backup", "gemini-main"], client.ModelChain("gemini", "gemini-main"));
        clock += TimeSpan.FromHours(7);
        Assert.Equal(["gemini-main", "gemini-backup"], client.ModelChain("gemini", "gemini-main"));
    }

    [Fact]
    public async Task Network_failures_are_transient_and_repeat_the_chain()
    {
        var handler = new FakeAiHandler().Throw(new HttpRequestException("conexão caiu")).Throw(new HttpRequestException("conexão caiu")).Gemini("voltou");
        var (client, pauses) = Build(handler);
        Assert.Equal("voltou", await client.TextAsync("p", Call));
        Assert.Single(pauses);
    }

    [Fact]
    public async Task Tokenharbor_is_the_last_resort_only_with_a_key_and_uses_its_own_credentials()
    {
        var handler = new FakeAiHandler().Reply(503, "{}").Reply(503, "{}")
            .Reply(200, """{"choices":[{"message":{"content":"```json\n{\"ok\": true}\n```"},"finish_reason":"stop"}]}""");
        var (client, _) = Build(handler, th: () => ("TH-KEY", ["deepseek:free"]), rounds: 0);
        string? used = null;
        var result = await client.JsonAsync("p", Schema, Call, onModel: m => used = m);
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal("tokenharbor/deepseek:free", used);
        Assert.Equal(("tokenharbor", "deepseek:free"), AiClient.SplitModelLabel("gemini", used!));
        var last = handler.Requests[2];
        Assert.Equal("tokenharbor.ai", last.Host);
        Assert.Equal("TH-KEY", last.Bearer);
        Assert.Contains("JSON Schema", last.Body["messages"]![0]!["content"]!.ToString()); // o schema vai no prompt
        Assert.DoesNotContain(handler.Requests.Take(2), r => r.Host == "tokenharbor.ai");

        var without = new FakeAiHandler().Reply(503, "{}").Reply(503, "{}");
        var (client2, _) = Build(without, rounds: 0);
        await Assert.ThrowsAsync<AiUnavailableException>(() => client2.TextAsync("p", Call));
        Assert.Equal(2, without.Requests.Count);
    }

    [Fact]
    public async Task Json_is_repaired_when_invalid_or_truncated_and_gives_up_as_a_response_error()
    {
        var handler = new FakeAiHandler().Gemini("isto não é json").Gemini("{\"parcial\":", finish: "MAX_TOKENS").Gemini("{\"ok\": true}");
        var (client, _) = Build(handler);
        Assert.True((await client.JsonAsync("p", Schema, Call, maxOutputTokens: 1000))["ok"]!.GetValue<bool>());
        Assert.Equal(1000, handler.Requests[1].Body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(2000, handler.Requests[2].Body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>()); // truncou: dobra o orçamento

        var truncated = new FakeAiHandler().Gemini("{", finish: "MAX_TOKENS").Gemini("{", finish: "MAX_TOKENS").Gemini("{\"ok\": true}");
        var (client2, _) = Build(truncated);
        await client2.JsonAsync("p", Schema, Call, maxOutputTokens: 1000);
        Assert.Equal([1000, 2000, 4000], truncated.Requests.Select(r => r.Body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>()));

        var hopeless = new FakeAiHandler();
        for (var i = 0; i < 6; i++) hopeless.Gemini("lixo");
        var (client3, pauses) = Build(hopeless);
        var ex = await Assert.ThrowsAsync<AiResponseException>(() => client3.JsonAsync("p", Schema, Call));
        Assert.Contains("resposta inutilizável", ex.Message);
        Assert.Empty(pauses); // JSON inutilizável não melhora esperando segundos
    }

    [Fact]
    public async Task Gemini_400_retries_once_without_native_schema()
    {
        var handler = new FakeAiHandler().Reply(400, """{"error":{"message":"responseJsonSchema unsupported"}}""").Gemini("{\"ok\": true}");
        var (client, _) = Build(handler);
        await client.JsonAsync("p", Schema, Call);
        Assert.NotNull(handler.Requests[0].Body["generationConfig"]!["responseJsonSchema"]);
        Assert.Null(handler.Requests[1].Body["generationConfig"]!["responseJsonSchema"]);
        Assert.Equal("application/json", handler.Requests[1].Body["generationConfig"]!["responseMimeType"]!.ToString());
    }

    [Fact]
    public async Task Openai_uses_the_responses_api_with_strict_schema()
    {
        var handler = new FakeAiHandler().Reply(200, """{"output":[{"content":[{"type":"output_text","text":"{\"ok\": true}"}]}],"usage":{"input_tokens":10,"output_tokens":5}}""");
        var (client, _) = Build(handler);
        await client.JsonAsync("p", Schema, new AiCall("openai", "gpt-4.1", "sk-test"));
        var sent = handler.Requests[0];
        Assert.Equal("api.openai.com", sent.Host);
        Assert.Equal("sk-test", sent.Bearer);
        Assert.False(sent.Body["store"]!.GetValue<bool>());
        Assert.True(sent.Body["text"]!["format"]!["strict"]!.GetValue<bool>());
        Assert.Equal("ok", sent.Body["text"]!["format"]!["schema"]!["required"]![0]!.ToString());
        Assert.Equal(0.0, sent.Body["temperature"]!.GetValue<double>()); // só modelos gpt-4 recebem temperatura
    }

    [Fact]
    public void Strictify_closes_every_object_and_requires_all_properties_without_touching_the_original()
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"},"items":{"type":"array","items":{"type":"object","properties":{"x":{"type":"integer"}}}}}}""")!.AsObject();
        var strict = AiClient.Strictify(schema);
        Assert.False(strict["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["a", "items"], strict["required"]!.AsArray().Select(n => n!.ToString()));
        Assert.False(strict["properties"]!["items"]!["items"]!["additionalProperties"]!.GetValue<bool>());
        Assert.Null(schema["additionalProperties"]);
    }

    [Fact]
    public void Model_list_and_label_helpers_match_python()
    {
        Assert.Equal(["a", "b"], AiClient.ParseModelList(" a,\n b , a,, "));
        Assert.Equal(("gemini", "tokenharbor-x"), AiClient.SplitModelLabel("gemini", "tokenharbor-x"));
        Assert.Equal(("gemini", "tokenharbor/"), AiClient.SplitModelLabel("gemini", "tokenharbor/"));
    }

    [Fact]
    public async Task Usage_is_recorded_per_provider_and_day_in_the_python_json_format()
    {
        using var t = new TempDb();
        var handler = new FakeAiHandler().Gemini("ok", tin: 120, tout: 30).Reply(503, "{}").Reply(503, "{}");
        var (client, _) = Build(handler, new UsageTracker(t.Db), rounds: 0);
        await client.TextAsync("p", Call);
        await Assert.ThrowsAsync<AiUnavailableException>(() => client.TextAsync("p", Call));
        var store = JsonNode.Parse(new SettingsStore(t.Db).Get("ai_usage_json"))!.AsObject();
        var day = store["days"]![$"gemini|{DateTime.UtcNow:yyyy-MM-dd}"]!;
        Assert.Equal(1, day["calls_ok"]!.GetValue<int>());
        Assert.Equal(2, day["calls_error"]!.GetValue<int>());
        Assert.Equal(2, day["calls_quota_error"]!.GetValue<int>()); // 503 conta como erro de cota
        Assert.Equal(120, day["tokens_in"]!.GetValue<int>());
        Assert.Equal(30, day["tokens_out"]!.GetValue<int>());
        Assert.Equal("gemini-backup", day["last_model"]!.ToString());
        Assert.Contains(t.Changes, c => c is QuotaChanged);
        Assert.DoesNotContain(" ", new SettingsStore(t.Db).Get("ai_usage_json").Replace("gemini-backup", "")); // JSON compacto como o Python
        Assert.Equal(1, UsageReader.Read(t.Db).Ai.Day.CallsOk);
    }
}

public class UsageTrackerTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Tokens_follow_the_gemini_and_openai_shapes()
    {
        Assert.Equal((10, 4), UsageTracker.ExtractTokens("gemini", Obj("""{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":4}}""")));
        Assert.Equal((10, 15), UsageTracker.ExtractTokens("gemini", Obj("""{"usageMetadata":{"promptTokenCount":10,"totalTokenCount":25}}""")));
        Assert.Equal((0, 7), UsageTracker.ExtractTokens("gemini", Obj("""{"usageMetadata":{"totalTokenCount":7}}""")));
        Assert.Equal((3, 2), UsageTracker.ExtractTokens("OpenAI", Obj("""{"usage":{"input_tokens":3,"output_tokens":2}}""")));
        Assert.Equal((3, 2), UsageTracker.ExtractTokens("openai", Obj("""{"usage":{"prompt_tokens":3,"completion_tokens":2}}""")));
        Assert.Equal((0, 0), UsageTracker.ExtractTokens("tokenharbor", Obj("""{"usage":{"input_tokens":3}}""")));
        Assert.Equal((0, 0), UsageTracker.ExtractTokens("gemini", null));
    }

    [Fact]
    public void Old_days_and_months_are_pruned_and_unknown_fields_survive()
    {
        var store = UsageTracker.Load("""{"days":{},"months":{},"extra":"fica"}""");
        for (var i = 1; i <= 50; i++)
            UsageTracker.RecordCall(store, "gemini", "m", 1, 1, true, false, new DateTime(2026, 1, 1).AddDays(i));
        Assert.Equal(45, store["days"]!.AsObject().Count);
        Assert.Equal(2, store["months"]!.AsObject().Count);
        Assert.Equal("fica", store["extra"]!.ToString());
        Assert.False(store["days"]!.AsObject().ContainsKey("gemini|2026-01-02")); // as mais antigas saíram
        UsageTracker.RecordCall(store, "", "", 0, 0, false, true, new DateTime(2026, 3, 1));
        Assert.True(store["days"]!.AsObject().ContainsKey("unknown|2026-03-01"));
    }

    [Fact]
    public void Corrupt_store_starts_over()
    {
        Assert.Empty(UsageTracker.Load("{ não é json")["days"]!.AsObject());
        Assert.Empty(UsageTracker.Load("[1,2]")["months"]!.AsObject());
    }
}
