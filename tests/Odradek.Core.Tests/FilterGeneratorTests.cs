using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.Core.Tests;

/// <summary>Paridade com a geração e a verificação do filtro LinkedIn do <c>app.py</c> (a IA é simulada; o perfil é o mesclado dos testes de currículo).</summary>
public class FilterGeneratorTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "filter_gen_cases.json")))!;
    private static readonly JsonObject Profile = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "resume_profile_cases.json")))!["merged"]!;

    private static readonly JsonObject ProfilePt = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "resume_profile_cases.json")))!["profile_pt"]!;

    private static Dictionary<string, string> Cfg(JsonObject source) => source.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());

    [Fact]
    public void Effective_prompts_and_rendering_match_python()
    {
        foreach (var item in Cases["effective"]!.AsArray().OfType<JsonObject>())
        {
            var cfg = Cfg(item["cfg"]!.AsObject());
            Assert.Equal(item["main"]!.GetValue<string>(), FilterGenerator.EffectiveMainPrompt(cfg));
            Assert.Equal(item["secondary"]!.GetValue<string>(), FilterGenerator.EffectiveSecondaryPrompt(cfg));
        }
        foreach (var item in Cases["render"]!.AsArray().OfType<JsonObject>())
        {
            var a = item["args"]!.AsObject();
            string Arg(string key) => a[key]?.GetValue<string>() ?? "";
            var actual = FilterGenerator.RenderPrompt(Arg("template"), Arg("pref_keywords"), Arg("facts_pt"), Arg("facts_en"), Arg("resume_pt"), Arg("resume_en"), Arg("profile"), Arg("area_hint"), Arg("main_filter"));
            Assert.True(item["result"]!.GetValue<string>() == actual, a.ToJsonString());
        }
    }

    [Fact]
    public async Task Generated_filters_prompts_logs_and_errors_match_python()
    {
        foreach (var item in Cases["cases"]!.AsArray().OfType<JsonObject>())
        {
            using var t = new TempDb();
            var cfg = Cfg(item["cfg"]!.AsObject());
            var slot = item["slot"]!.GetValue<string>();
            var handler = new FakeAiHandler().Gemini(item["decision"]!.ToJsonString());
            var generator = new FilterGenerator(t.Db, new AiClient(new HttpClient(handler)), _ => "KEY-gemini");
            var name = item["name"]!.GetValue<string>();
            if (item["error"] is { } error)
            {
                var ex = await Assert.ThrowsAsync<ActionException>(() => generator.GenerateWithAsync(Profile, cfg, slot, nowIso: "2026-06-01T12:00:00+00:00"));
                Assert.True(error.GetValue<string>() == "ValueError: " + ex.Message, name);
            }
            else
            {
                var result = await generator.GenerateWithAsync(Profile, cfg, slot, nowIso: "2026-06-01T12:00:00+00:00");
                Assert.True(item["result"]!.GetValue<string>() == PyJson.Serialize(result), $"{name}\n{item["result"]}\n{PyJson.Serialize(result)}");
            }
            var request = handler.Requests.Single();
            var call = item["call"]!.AsObject();
            Assert.True(call["prompt"]!.GetValue<string>() == request.Body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>(), $"{name}: prompt");
            Assert.Equal(call["max_output_tokens"]!.GetValue<int>(), request.Body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());
            var logs = t.Db.Open().Query<(string Level, string Source, string Message)>("SELECT level, source, message FROM event_logs ORDER BY id").Select(l => new[] { l.Level, l.Source, l.Message }).ToList();
            Assert.Equal(item["logs"]!.AsArray().Select(l => l!.AsArray().Select(x => x!.GetValue<string>()).ToArray()), logs);
        }
    }

    [Fact]
    public async Task Without_a_resume_or_a_key_generation_explains_what_is_missing()
    {
        using var t = new TempDb();
        var handler = new FakeAiHandler();
        var noResume = new FilterGenerator(t.Db, new AiClient(new HttpClient(handler)), _ => "KEY");
        var error = await Assert.ThrowsAsync<ActionException>(() => noResume.GenerateAsync(new Dictionary<string, string>(), "main"));
        Assert.StartsWith("Analise um currículo antes de gerar o filtro LinkedIn/Apify", error.Message);

        var noKey = new FilterGenerator(t.Db, new AiClient(new HttpClient(handler)), _ => "");
        var missing = await Assert.ThrowsAsync<AiUnavailableException>(() => noKey.GenerateWithAsync(Profile, new Dictionary<string, string>(), "main"));
        Assert.Equal("Configure a chave da API de IA para gerar o filtro LinkedIn.", missing.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Generate_and_save_replaces_only_the_requested_slot_after_saving_the_form()
    {
        using var t = new TempDb();
        using (var c = t.Db.Open())
            c.Execute("INSERT INTO resumes(language,original_filename,stored_path,file_sha256,extracted_text,analysis_status,profile_json,profile_version,created_at,updated_at) VALUES('pt','cv.pdf','x','sha','texto','ok',@profile,'2','x','x')",
                new { profile = ProfilePt.ToJsonString() });
        var secondary = LinkedInFilters.ValidateForm("""{"name":"Reserva","keywords":["Suporte"]}""", "secondary");
        new SettingsStore(t.Db).Set(LinkedInFilters.SecondaryKey, secondary);
        var handler = new FakeAiHandler().Gemini("""{"filter":{"name":"Analista","keywords":[{"phrase":"Analista de Sistemas","evidence_ids":["R1"]}],"experience_levels":[3]}}""");
        var generator = new FilterGenerator(t.Db, new AiClient(new HttpClient(handler)), _ => "KEY");
        var generated = await generator.GenerateAndSaveAsync(new Dictionary<string, string> { ["keywords"] = "python, c#" }, "main");
        var store = new SettingsStore(t.Db);
        Assert.Equal("python, c#", store.Get("keywords"));
        Assert.Equal(PyJson.Serialize(generated), store.Get(LinkedInFilters.MainKey));
        Assert.Equal(secondary, store.Get(LinkedInFilters.SecondaryKey)); // o outro slot não muda
        Assert.Equal("IA gerou o filtro Principal: Analista de Sistemas.", t.Scalar<string>("SELECT message FROM event_logs WHERE source='apify' ORDER BY id DESC LIMIT 1"));
    }
}
