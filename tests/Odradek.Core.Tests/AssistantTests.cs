using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Engines;

namespace Odradek.Core.Tests;

public class AssistantTests : IDisposable
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "assistant_cases.json")))!;
    private readonly TempDb _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Dictionary<string, string> Cfg(string key) => Cases[key]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());

    [Fact]
    public void Panel_profile_block_matches_python()
    {
        Assert.Equal(Cases["profile_block"]!["full"]!.GetValue<string>(), Assistant.PanelProfileBlock(Cfg("cfg_full")));
        Assert.Equal(Cases["profile_block"]!["min"]!.GetValue<string>(), Assistant.PanelProfileBlock(Cfg("cfg_min")));
        Assert.Equal(Cases["profile_block"]!["none"]!.GetValue<string>(), Assistant.PanelProfileBlock(null));
    }

    [Fact]
    public void Context_matches_python_for_every_resume_and_config_combination()
    {
        foreach (var item in Cases["contexts"]!.AsArray().OfType<JsonObject>())
        {
            var rows = Cases["db_rows"]![item["db"]!.GetValue<string>()]!.AsArray()
                .ToDictionary(r => r![0]!.GetValue<string>(), r => (r![1]!.GetValue<string>(), r![2]!.GetValue<string>()));
            var cfg = item["cfg"]!.GetValue<string>() switch { "full" => Cfg("cfg_full"), "min" => Cfg("cfg_min"), _ => [] };
            var actual = Assistant.BuildContext(cfg, lang => rows.TryGetValue(lang, out var row) ? row : null);
            Assert.True(item["context"]!.GetValue<string>() == actual, $"{item["db"]}/{item["cfg"]}: contexto difere (Python {item["context"]!.GetValue<string>().Length}, C# {actual.Length})");
        }
    }

    [Fact]
    public void History_normalization_matches_python()
    {
        foreach (var (name, input) in Cases["histories_in"]!.AsObject())
        {
            var expected = Cases["histories_out"]![name]!.AsArray().Select(t => (t!["role"]!.GetValue<string>(), t["content"]!.GetValue<string>())).ToList();
            var actual = Assistant.NormalizeHistory(input?.DeepClone()).Select(t => (t.Role, t.Content)).ToList();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Prompt_matches_python()
    {
        foreach (var item in Cases["prompts"]!.AsArray().OfType<JsonObject>())
        {
            var history = item["history"] is JsonArray turns ? turns.Select(t => new Assistant.Turn(t!["role"]!.GetValue<string>(), t["content"]!.GetValue<string>())).ToList() : null;
            var actual = Assistant.BuildPrompt(item["question"]!.GetValue<string>(), item["context"]!.GetValue<string>(), history);
            Assert.True(item["prompt"]!.GetValue<string>() == actual, "prompt do assistente difere do Python");
        }
    }

    private (AiClient Client, FakeAiHandler Handler) Ai(Action<FakeAiHandler> script)
    {
        var handler = new FakeAiHandler();
        script(handler);
        return (new AiClient(new HttpClient(handler), null, null, new AiClientOptions { FallbackModels = [], RetryDelays = [], Pause = (_, _) => Task.CompletedTask }), handler);
    }

    [Fact]
    public async Task Answer_uses_the_panel_context_and_history_and_never_sends_the_key_in_the_prompt()
    {
        new Data.SettingsStore(_temp.Db).SetMany(new Dictionary<string, string> { ["candidate_name"] = "Ana Exemplo", ["candidate_city"] = "Belo Horizonte" });
        using (var c = _temp.Db.Open())
            c.Execute("INSERT INTO resumes(language,stored_path,file_sha256,analysis_summary,created_at,updated_at) VALUES('pt','x','s','Resumo curto','x','x')");
        var (client, handler) = Ai(h => h.Gemini("  Ana Exemplo \n"));
        var history = JsonNode.Parse("""[{"role":"user","content":"oi"},{"role":"assistant","content":"olá"}]""");
        var answer = await new Assistant(_temp.Db, client, _ => "SECRET-KEY-42").AnswerAsync("Qual é o seu nome?", history);
        Assert.Equal("Ana Exemplo", answer);
        var prompt = handler.Requests[0].Body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("name: Ana Exemplo", prompt);
        Assert.Contains("Resume dossier (PT):\nResumo curto", prompt);
        Assert.Contains("Prior turns in this chat:\nUser: oi\nAssistant: olá", prompt);
        Assert.DoesNotContain("SECRET-KEY-42", prompt);
        Assert.Equal(0.2, handler.Requests[0].Body["generationConfig"]!["temperature"]!.GetValue<double>());
        Assert.Contains(_temp.Changes, c => c is Data.LogAppended);
    }

    [Fact]
    public async Task Answer_validates_the_question_and_the_key_before_calling_the_ai()
    {
        var (client, handler) = Ai(_ => { });
        var assistant = new Assistant(_temp.Db, client, _ => "");
        Assert.Equal("Cole a pergunta do formulário.", (await Assert.ThrowsAsync<ActionException>(() => assistant.AnswerAsync("  "))).Message);
        Assert.Equal("Pergunta muito longa (máx. 4000 caracteres).", (await Assert.ThrowsAsync<ActionException>(() => assistant.AnswerAsync(new string('x', 4001)))).Message);
        Assert.Equal("IA indisponível: chave não configurada.", (await Assert.ThrowsAsync<AiUnavailableException>(() => assistant.AnswerAsync("oi"))).Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Empty_ai_reply_is_an_error_and_long_replies_are_cut_to_2000_characters()
    {
        var (client, _) = Ai(h => h.Gemini(new string('a', 2500)));
        Assert.Equal(2000, (await new Assistant(_temp.Db, client, _ => "k").AnswerAsync("oi")).Length);
        var (blank, _) = Ai(h => h.Gemini("   "));
        Assert.Equal("A IA não retornou resposta.", (await Assert.ThrowsAsync<ActionException>(() => new Assistant(_temp.Db, blank, _ => "k").AnswerAsync("oi"))).Message);
    }
}
