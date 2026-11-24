using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Ai;
using Odradek.Core.Data;
using Odradek.Core.Engines;
using Odradek.Core.Queue;

namespace Odradek.Core.Tests;

/// <summary>Triagem completa sobre um banco temporário, com a IA simulada por <see cref="FakeAiHandler"/> (nenhuma chamada de rede).</summary>
public class TriageServiceTests : IDisposable
{
    private static readonly JsonObject Profiles = Load("resume_profile_cases.json");
    private static readonly JsonObject Assess = Load("assessment_cases.json");
    private readonly TempDb _temp = new();

    public void Dispose() => _temp.Dispose();

    private static JsonObject Load(string name) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!;
    private static JsonObject Case(string name) => Assess["cases"]!.AsArray().OfType<JsonObject>().Single(c => c["name"]!.GetValue<string>() == name);

    private void SeedResumes(bool analyzed = true, bool portfolio = true)
    {
        using var c = _temp.Db.Open();
        foreach (var (lang, profile, text) in new[] { ("pt", "profile_pt", "resume_pt"), ("en", "profile_en", "resume_en") })
            c.Execute(
                "INSERT INTO resumes(language,stored_path,file_sha256,analysis_summary,extracted_text,profile_json,profile_version,created_at,updated_at) VALUES(@lang,'x','sha',@summary,@text,@profile,'2','x','x')",
                new { lang, summary = analyzed ? $"Resumo {lang}" : "", text = Profiles[text]!.GetValue<string>(), profile = Profiles[profile]!.ToJsonString() });
        if (portfolio)
        {
            var snapshot = new JsonObject { ["pt"] = Profiles["portfolio"]!.DeepClone(), ["en"] = Profiles["portfolio"]!.DeepClone() };
            new SettingsStore(_temp.Db).SetMany(new Dictionary<string, string> { ["portfolio_profile_json"] = snapshot.ToJsonString(), ["portfolio_text"] = "" });
        }
    }

    private long AddJob(string language = "pt", string location = "Remoto, Brasil", string url = "https://jobs.example.test/1", string source = "test",
        string extra = "", string filter = "", string status = "review")
    {
        var main = Case("main_approved_no_objection")["job"]!;
        using var c = _temp.Db.Open();
        return c.ExecuteScalar<long>(
            @"INSERT INTO jobs(source,title,company,location,description,url,first_seen_at,status,fingerprint,language,search_filter_id,search_remote)
              VALUES(@source,@title,'Empresa Teste',@location,@description,@url,'2026-11-20T12:00:00+00:00',@status,hex(randomblob(8)),@language,@filter,'1') RETURNING id",
            new { source, title = main["title"]!.GetValue<string>(), location, description = main["description"]!.GetValue<string>() + extra, url, status, language, filter });
    }

    private static (AiClient Client, FakeAiHandler Handler) Ai(Action<FakeAiHandler> script)
    {
        var handler = new FakeAiHandler();
        script(handler);
        var options = new AiClientOptions { FallbackModels = [], RetryDelays = [], Pause = (_, _) => Task.CompletedTask };
        return (new AiClient(new HttpClient(handler), null, null, options), handler);
    }

    private static void Approved(FakeAiHandler h, string objections = "{\"objections\":[]}") =>
        h.Gemini(Case("main_approved_no_objection")["raw_assess"]!.ToJsonString()).Gemini(objections).Gemini("Prezados, segue minha candidatura.");

    private string Scalar(string sql, object? args = null)
    {
        using var c = _temp.Db.Open();
        return c.ExecuteScalar<string?>(sql, args) ?? "";
    }

    [Fact]
    public async Task Approved_job_with_a_contact_email_becomes_worth_with_a_letter_and_sends_nothing()
    {
        SeedResumes();
        var id = AddJob(extra: "<p>Envie o currículo para rh@empresa-teste.example</p>");
        var (client, handler) = Ai(h => Approved(h));
        var progress = new List<string>();
        var result = await new TriageService(_temp.Db, client, _ => "TEST-KEY").ProcessAutoJobAsync(id, progress.Add);

        Assert.True(result == $"vaga {id}: vale a pena olhar — e-mail de contato detectado, aguardando sua confirmação (score 85)", result + " | " + Scalar("SELECT reason FROM ai_decisions WHERE job_id=@id", new { id }));
        Assert.Equal(3, handler.Requests.Count); // auditoria, revisão cética, carta
        Assert.Equal("worth", Scalar("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.StartsWith("Vale a pena olhar (score 85/100). E-mail de contato detectado (rh@empresa-teste.example).", Scalar("SELECT notes FROM jobs WHERE id=@id", new { id }));
        Assert.Equal("Prezados, segue minha candidatura.", Scalar("SELECT body FROM cover_letters WHERE job_id=@id", new { id }));
        Assert.Equal("manual", Scalar("SELECT apply_channel FROM ai_decisions WHERE job_id=@id", new { id }));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM applications"));
        Assert.Equal("85", Scalar("SELECT match_score FROM ai_decisions WHERE job_id=@id", new { id }));
        Assert.Equal("v2.1", Scalar("SELECT scoring_version FROM ai_decisions WHERE job_id=@id", new { id }));
        Assert.Equal("pt", Scalar("SELECT resume_language FROM ai_decisions WHERE job_id=@id", new { id }));
        Assert.Contains(progress, p => p.StartsWith("Decisão: APROVADA · score 85/100 · trilha core · CV pt · modelo gemini/"));
        Assert.Contains(_temp.Changes, c => c is JobsChanged j && j.Ids.Contains(id));

        // O assessment gravado é o que o Python calcula para as mesmas respostas da IA.
        var stored = JsonNode.Parse(Scalar("SELECT assessment_json FROM ai_decisions WHERE job_id=@id", new { id }))!;
        Assert.Equal(Case("main_approved_no_objection")["expected"]!["assessment"]!["decision"]!["score"]!.GetValue<int>(), stored["decision"]!["score"]!.GetValue<int>());
        Assert.Equal("v2.1", stored["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task Job_without_a_contact_email_records_a_blocked_application_and_waits_for_the_extension()
    {
        SeedResumes();
        var id = AddJob();
        var (client, _) = Ai(h => Approved(h));
        var result = await new TriageService(_temp.Db, client, _ => "k").ProcessAutoJobAsync(id);
        Assert.Equal($"vaga {id}: vale a pena olhar — Nenhum e-mail de contato encontrado na vaga.", result);
        Assert.Equal("worth", Scalar("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.Equal("blocked|none", Scalar("SELECT status||'|'||channel FROM applications WHERE job_id=@id", new { id }));
    }

    [Fact]
    public async Task LinkedIn_job_goes_to_worth_looking_for_manual_application()
    {
        SeedResumes();
        var id = AddJob(url: "https://www.linkedin.com/jobs/view/123", source: "linkedin");
        var (client, _) = Ai(h => Approved(h));
        var result = await new TriageService(_temp.Db, client, _ => "k").ProcessAutoJobAsync(id);
        Assert.Equal($"vaga {id}: vale a pena olhar (LinkedIn, score 85)", result);
        Assert.Contains("Vaga do LinkedIn — carta preparada.", Scalar("SELECT notes FROM jobs WHERE id=@id", new { id }));
    }

    [Fact]
    public async Task A_verified_blocker_from_the_skeptic_discards_the_job_without_writing_a_letter()
    {
        SeedResumes();
        var id = AddJob();
        var skeptic = Case("main_blocker")["raw_skeptic"]!.ToJsonString();
        var (client, handler) = Ai(h => h.Gemini(Case("main_blocker")["raw_assess"]!.ToJsonString()).Gemini(skeptic));
        var result = await new TriageService(_temp.Db, client, _ => "k").ProcessAutoJobAsync(id);
        Assert.Equal($"IA descartou vaga {id} (85/100)", result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("ignored", Scalar("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.StartsWith("IA: score 85/100. ", Scalar("SELECT notes FROM jobs WHERE id=@id", new { id }));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM cover_letters"));
        Assert.Equal("0", Scalar("SELECT should_apply FROM ai_decisions WHERE job_id=@id", new { id }));
    }

    [Fact]
    public async Task Uncertain_language_blocks_the_job_before_any_ai_call()
    {
        SeedResumes();
        var id = AddJob(language: "unknown");
        var (client, handler) = Ai(_ => { });
        var result = await new TriageService(_temp.Db, client, _ => "k").ProcessAutoJobAsync(id);
        Assert.Equal($"vaga {id}: Idioma incerto; a IA não vai escolher um currículo por suposição.", result);
        Assert.Equal("blocked", Scalar("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Without_an_analyzed_resume_the_job_is_blocked()
    {
        SeedResumes(analyzed: false);
        var id = AddJob();
        var (client, handler) = Ai(_ => { });
        var result = await new TriageService(_temp.Db, client, _ => "k").ProcessAutoJobAsync(id);
        Assert.Equal($"vaga {id}: Faça upload e análise de pelo menos um currículo antes da triagem automática.", result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Excluded_region_is_ignored_without_calling_the_ai()
    {
        SeedResumes();
        var id = AddJob(location: "Bengaluru, India");
        var (client, handler) = Ai(_ => { });
        var result = await new TriageService(_temp.Db, client, _ => "k").ProcessAutoJobAsync(id);
        Assert.Equal($"vaga {id}: região excluída", result);
        Assert.Equal("ignored", Scalar("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.Equal("Região excluída: local da vaga na Índia (Bengaluru, India).", Scalar("SELECT notes FROM jobs WHERE id=@id", new { id }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Missing_api_key_surfaces_as_ai_unavailable_so_the_queue_retries_and_leaves_the_job_untouched()
    {
        SeedResumes();
        var id = AddJob();
        var (client, handler) = Ai(_ => { });
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => new TriageService(_temp.Db, client, _ => "").ProcessAutoJobAsync(id));
        Assert.Equal("Configure a chave da API de IA para ativar o julgamento automático.", ex.Message);
        Assert.Equal("review", Scalar("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Letter_failure_blocks_the_job_but_keeps_the_decision()
    {
        SeedResumes();
        var id = AddJob();
        var (client, _) = Ai(h => h.Gemini(Case("main_approved_no_objection")["raw_assess"]!.ToJsonString()).Gemini("{\"objections\":[]}").Gemini("   "));
        var result = await new TriageService(_temp.Db, client, _ => "k").ProcessAutoJobAsync(id);
        Assert.Equal($"vaga {id}: falha na carta", result);
        Assert.Equal("blocked", Scalar("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.StartsWith("Falha ao gerar carta:", Scalar("SELECT notes FROM jobs WHERE id=@id", new { id }));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM ai_decisions WHERE job_id=@id", new { id }));
    }

    [Fact]
    public async Task Removed_job_is_a_noop_and_the_queue_handler_wires_the_payload()
    {
        SeedResumes();
        var (client, _) = Ai(_ => { });
        var service = new TriageService(_temp.Db, client, _ => "k");
        var lines = new List<string>();
        var result = await service.ApplyHandler(new QueueJob(1, "job_apply", new JsonObject { ["job_id"] = 9999 }, lines.Add), CancellationToken.None);
        Assert.Equal("vaga removida", result);
        Assert.Equal(["Vaga #9999 não existe mais; nada a fazer"], lines);
        await Assert.ThrowsAsync<NonRetryableException>(() => service.ApplyHandler(new QueueJob(1, "job_apply", [], _ => { }), CancellationToken.None));
    }

    [Fact]
    public async Task Cover_letter_prompt_carries_the_facts_summary_bridges_and_never_leaks_the_key()
    {
        SeedResumes();
        var id = AddJob(status: "new");
        new SettingsStore(_temp.Db).SetMany(new Dictionary<string, string> { ["candidate_name"] = "Ana Exemplo", ["candidate_facts_pt"] = "Prefere remoto." });
        var (client, handler) = Ai(h => h.Gemini("Carta pronta."));
        var letterId = await new TriageService(_temp.Db, client, _ => "SECRET-KEY-999").CreateCoverLetterAsync(id, "pt", ["Suporte interno ← atendimento"]);
        var prompt = handler.Requests[0].Body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("cover letter in Portuguese", prompt);
        Assert.Contains("Candidate name: Ana Exemplo", prompt);
        Assert.Contains("Prefere remoto.", prompt);
        Assert.Contains("- Suporte interno ← atendimento", prompt);
        Assert.Contains("Resume analysis summary (authoritative; do not invent beyond this):\nResumo pt", prompt);
        Assert.Contains(TriageService.ExperiencePriorityRule, prompt);
        Assert.DoesNotContain("<p>", prompt);
        Assert.DoesNotContain("SECRET-KEY-999", prompt);
        Assert.Equal("prepared", Scalar("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.Equal(letterId.ToString(), Scalar("SELECT id FROM cover_letters WHERE job_id=@id", new { id }));
        // Regerar substitui a carta em vez de duplicar.
        var (client2, _) = Ai(h => h.Gemini("Segunda versão."));
        Assert.Equal(letterId, await new TriageService(_temp.Db, client2, _ => "k").CreateCoverLetterAsync(id, "pt"));
        Assert.Equal("Segunda versão.", Scalar("SELECT body FROM cover_letters WHERE job_id=@id", new { id }));
    }

    [Theory]
    [InlineData("sec-123", "transferable")]
    [InlineData("tr-9", "transferable")]
    [InlineData("main-1", "core")]
    [InlineData("", "core")]
    [InlineData(null, "core")]
    public void Secondary_filter_ids_use_the_transferable_track(string? filter, string track) => Assert.Equal(track, TriageService.JobTrack(filter));
}
