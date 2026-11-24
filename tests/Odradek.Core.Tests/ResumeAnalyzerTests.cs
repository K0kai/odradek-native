using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Ai;
using Odradek.Core.Data;
using Odradek.Core.Engines;
using Odradek.Core.Queue;

namespace Odradek.Core.Tests;

public class ResumeAnalyzerTests : IDisposable
{
    private static readonly JsonObject Profiles = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "resume_profile_cases.json")))!;
    private readonly TempDb _temp = new();

    public void Dispose() => _temp.Dispose();

    private void SeedPending(string text)
    {
        using var c = _temp.Db.Open();
        c.Execute("INSERT INTO resumes(language,original_filename,stored_path,file_sha256,extracted_text,analysis_status,created_at,updated_at) VALUES('pt','cv.pdf','x','sha',@text,'pending','x','x')", new { text });
    }

    private static (AiClient Client, FakeAiHandler Handler) Ai(Action<FakeAiHandler> script)
    {
        var handler = new FakeAiHandler();
        script(handler);
        return (new AiClient(new HttpClient(handler), null, null, new AiClientOptions { FallbackModels = [], RetryDelays = [], Pause = (_, _) => Task.CompletedTask }), handler);
    }

    private T Row<T>(string column)
    {
        using var c = _temp.Db.Open();
        return c.ExecuteScalar<T>($"SELECT {column} FROM resumes WHERE language='pt'")!;
    }

    private static QueueJob Job() => new(1, "resume_analysis", new JsonObject { ["language"] = "PT" }, _ => { });

    [Fact]
    public async Task Handler_analyses_the_stored_text_and_saves_analysis_profile_and_status()
    {
        SeedPending(Profiles["resume_pt"]!.GetValue<string>());
        var (client, handler) = Ai(h => h.Gemini(Profiles["raw_pt"]!.ToJsonString()));
        var message = await new ResumeAnalyzer(_temp.Db, client, _ => "TEST-KEY").Handler(Job(), CancellationToken.None);

        Assert.Equal("Currículo PT: análise da IA concluída (gemini/gemini-3.5-flash-lite).", message);
        Assert.Equal("ok", Row<string>("analysis_status"));
        Assert.Equal("2", Row<string>("profile_version"));
        Assert.Equal("", Row<string>("analysis_error"));
        var summary = Row<string>("analysis_summary");
        Assert.StartsWith("Headline: Desenvolvedora de software com foco em automação", summary);
        var analysis = JsonNode.Parse(Row<string>("analysis_json"))!.AsObject();
        Assert.False(analysis.ContainsKey("_profile"));
        Assert.Equal(summary, analysis["summary"]!.GetValue<string>());
        var profile = JsonNode.Parse(Row<string>("profile_json"))!.AsObject();
        Assert.Equal(2, profile["roles"]!.AsArray().Count); // o cargo inventado fica de fora
        Assert.Contains(_temp.Changes, c => c is ResumesChanged);
        // O prompt vai com a estrutura de verificação, mas a chave de API nunca entra no texto.
        var prompt = handler.Requests[0].Body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Resume text:", prompt);
        Assert.DoesNotContain("TEST-KEY", prompt);
    }

    [Fact]
    public async Task Missing_key_is_retried_by_the_queue_and_does_not_mark_the_resume_as_failed()
    {
        SeedPending(Profiles["resume_pt"]!.GetValue<string>());
        var (client, handler) = Ai(_ => { });
        await Assert.ThrowsAsync<AiUnavailableException>(() => new ResumeAnalyzer(_temp.Db, client, _ => "").Handler(Job(), CancellationToken.None));
        Assert.Equal("pending", Row<string>("analysis_status"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Reply_without_verifiable_facts_marks_the_resume_as_error_with_the_python_style_message()
    {
        SeedPending(Profiles["resume_pt"]!.GetValue<string>());
        var (client, _) = Ai(h => h.Gemini("""{"roles":[{"title":"Inventado","quote":"nada disso está no PDF"}],"skills":[]}"""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ResumeAnalyzer(_temp.Db, client, _ => "k").Handler(Job(), CancellationToken.None));
        Assert.Equal("A análise não retornou fatos verificáveis no texto do PDF.", ex.Message);
        Assert.Equal("error", Row<string>("analysis_status"));
        Assert.Equal("InvalidOperationError: A análise não retornou fatos verificáveis no texto do PDF.", Row<string>("analysis_error"));
        Assert.StartsWith("Currículo PT: falha na análise — ", Row<string>("analysis_message"));
    }

    [Fact]
    public async Task Unknown_resume_language_fails_without_touching_other_rows()
    {
        SeedPending("texto");
        var (client, _) = Ai(_ => { });
        var job = new QueueJob(1, "resume_analysis", new JsonObject { ["language"] = "en" }, _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ResumeAnalyzer(_temp.Db, client, _ => "k").Handler(job, CancellationToken.None));
        Assert.Equal("pending", Row<string>("analysis_status"));
    }
}
