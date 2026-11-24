using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Candidate;
using Odradek.Core.Data;
using Odradek.Core.Queue;

namespace Odradek.Core.Engines;

/// <summary>Dado da vaga ou da configuração que impede a triagem (o <c>ValueError</c> do Python): a vaga fica "blocked" com o motivo.</summary>
public sealed class TriageValidationException(string message) : Exception(message);

/// <summary>Resultado de <see cref="TriageService.AssessAsync"/>: a decisão mais o provedor, o modelo e o currículo que valeram.</summary>
public sealed record TriageDecision(JobAssessment.Outcome Outcome, string Provider, string Model, string ResumeLanguage, string ScoringVersion);

/// <summary>
/// Triagem automática de uma vaga (<c>process_auto_job</c>, <c>ai_assess_job</c>, <c>create_cover_letter</c> em <c>app.py</c>):
/// avalia, grava a decisão, gera a carta e move a vaga. A IA nunca envia e-mail sozinha.
/// </summary>
public sealed class TriageService(Database db, AiClient ai, Func<string, string>? secret = null)
{
    public const string DefaultJobSelectionPrompt = """
You are a fair, practical job-fit evaluator. Judge whether this candidate has a credible, evidence-backed path to succeed in this role.

- Judge the role by its real day-to-day responsibilities, not by an inflated wish-list of requirements.
- Give credit to transferable skills and adjacent experience only when the resume proves the underlying capability.
- Treat missing secondary or preferred tools as a modest gap, not an automatic rejection.
- Requirements that contradict the posting itself (e.g. an internship asking for many years of experience) should be disregarded.
- For internships and entry-level roles, judge potential at the level the posting itself describes: an in-progress degree and the portfolio projects cited in the profile count as evidence of skill.
- Silence in the posting about something (for example whether foreigners may apply, or the work model) is not a restriction. Treat a restriction as real only when the posting states it.
- Do not invent employers, degrees, skills, or experience absent from the candidate information.

""";

    public const string ExperiencePriorityRule = """
Experience coverage (CRITICAL — follow every time):
- Do NOT systematically ignore any role on the resume. If the candidate has
  multiple jobs, answers and letters should be able to draw on more than one
  when relevant — never write as if only a single employer existed.
- Choose what to emphasize by FIT to the question/job (stack, domain, impact),
  not by which role has more numbers and not by “always current” or “always oldest”.
- A strong past role (e.g. heavy automation impact) is worth mentioning when it
  supports the answer; the current role is worth mentioning when it shows what
  they do now / stack / employer. Prefer weaving both briefly over picking one
  and erasing the other.
- Still: never invent employers, dates, metrics, or skills. If space is tight
  (short form field), pick the single best-fitting fact — but across typical
  open answers and cover letters, avoid always defaulting to the same employer.
""";

    private readonly EventLogStore _log = new(db);
    private readonly Func<string, string> _secret = secret ?? (name => SecretStore.Get(name));

    private sealed class JobRow
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? Company { get; set; }
        public string? Location { get; set; }
        public string? Source { get; set; }
        public string? Url { get; set; }
        public string? Description { get; set; }
        public string? Language { get; set; }
        public string? SearchFilterId { get; set; }
        public string? SearchRemote { get; set; }
        public string? Status { get; set; }
    }

    private const string JobColumns = "id, title, company, location, source, url, description, language, search_filter_id, search_remote, status";

    private static AssessJob ToAssess(JobRow j) => new(j.Title ?? "", j.Company ?? "", j.Location ?? "", j.Description ?? "", j.Language ?? "", j.SearchRemote ?? "");

    // ------------------------------------------------------------ configuração

    public static string EffectivePrompt(IReadOnlyDictionary<string, string> cfg)
    {
        var custom = (cfg.GetValueOrDefault("job_selection_prompt") ?? "").Trim();
        return custom.Length > 0 ? custom : DefaultJobSelectionPrompt;
    }

    /// <summary>'transferable' quando a vaga veio do filtro Secundário (ids 'sec-', e 'tr-' do formato antigo).</summary>
    public static string JobTrack(string? searchFilterId)
    {
        var id = (searchFilterId ?? "").Trim();
        return id.StartsWith("sec-", StringComparison.Ordinal) || id.StartsWith("tr-", StringComparison.Ordinal) ? "transferable" : "core";
    }

    public static bool IsLinkedInJob(string? url, string? source, string? description, string? company)
    {
        var blob = string.Join(' ', url ?? "", source ?? "", description ?? "", company ?? "").ToLowerInvariant();
        return blob.Contains("linkedin.com", StringComparison.Ordinal) || (source ?? "").ToLowerInvariant().Contains("linkedin", StringComparison.Ordinal);
    }

    private (string Provider, string Model) ProviderAndModel(IReadOnlyDictionary<string, string> cfg) =>
        ((cfg.GetValueOrDefault("ai_provider") ?? "gemini").ToLowerInvariant(), (cfg.GetValueOrDefault("ai_model") ?? "gemini-3.5-flash-lite").Trim());

    // ------------------------------------------------------------ avaliação

    /// <summary>Triagem v2: requisitos × evidência verificada. Lança <see cref="TriageValidationException"/> ou <see cref="AiUnavailableException"/>.</summary>
    public async Task<TriageDecision> AssessAsync(AssessJob job, IReadOnlyDictionary<string, string> cfg, string track = "core", CallJson? callJson = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var say = progress ?? (_ => { });
        var language = job.Language;
        if (language is not ("pt" or "en")) throw new TriageValidationException("Idioma incerto; a IA não vai escolher um currículo por suposição.");
        Dictionary<string, string> summaries;
        using (var connection = db.Open()) summaries = ResumeData.Summaries(connection);
        var bundle = ResumeData.MergedProfile(db, language);
        var analyzed = summaries.Where(kv => kv.Value.Trim().Length > 0).Select(kv => kv.Key).ToHashSet();
        if (analyzed.Count == 0) throw new TriageValidationException("Faça upload e análise de pelo menos um currículo antes da triagem automática.");
        if (bundle is null)
            throw new AiUnavailableException("O perfil verificado do currículo ainda não foi gerado; a triagem tentará de novo após a reanálise.");
        var (profile, resumeNorm) = bundle.Value;

        var (provider, model) = ProviderAndModel(cfg);
        var lastModel = model;
        if (callJson is null)
        {
            var apiKey = _secret(provider);
            if (apiKey.Length == 0) throw new AiUnavailableException("Configure a chave da API de IA para ativar o julgamento automático.");
            var aiCall = new AiCall(provider, model, apiKey);
            callJson = (prompt, schema, max, thinking, token) =>
                ai.JsonAsync(prompt, schema, aiCall, maxOutputTokens: max, thinkingBudget: thinking, onModel: used => lastModel = used, ct: token);
        }
        say($"Avaliando com {provider}/{model}, trilha {track}, idioma da vaga {language}");
        var outcome = await JobAssessment.AssessAsync(job, cfg, profile, resumeNorm, callJson, language, EffectivePrompt(cfg), track, say, ct);
        // O currículo é escolhido pelo idioma da vaga (não pelo LLM); cai para o outro se ausente.
        var other = language == "pt" ? "en" : "pt";
        var resumeLanguage = analyzed.Contains(language) ? language : other;
        if (lastModel != model) say($"Modelo de fallback respondeu: {lastModel} (configurado: {model})");
        var (finalProvider, finalModel) = AiClient.SplitModelLabel(provider, lastModel); // "tokenharbor/x" → provedor tokenharbor, modelo x
        return new TriageDecision(outcome, finalProvider, finalModel, resumeLanguage, JobAssessment.ScoringVersion);
    }

    // ------------------------------------------------------------ carta

    /// <summary>Pede somente a carta; o modelo não recebe, cria ou altera o arquivo de currículo.</summary>
    public async Task<string> GenerateLetterAsync(string provider, string model, string language, AssessJob job, IReadOnlyDictionary<string, string> cfg,
        IReadOnlyList<string>? bridges = null, CancellationToken ct = default)
    {
        provider = provider.ToLowerInvariant().Trim();
        var languageName = language == "pt" ? "Portuguese" : "English";
        var facts = (cfg.GetValueOrDefault(language == "pt" ? "candidate_facts_pt" : "candidate_facts_en") ?? "").Trim();
        var name = (cfg.GetValueOrDefault("candidate_name") ?? "").Trim();
        var bridgeBlock = "";
        if (bridges is { Count: > 0 })
            bridgeBlock = "Verified links between the candidate's real experience and this job's requirements "
                + "(use them to connect transferable experience explicitly; do not go beyond them):\n"
                + string.Join("\n", bridges.Take(8).Select(item => $"- {item}")) + "\n";
        var summary = cfg.GetValueOrDefault(language == "pt" ? "resume_summary_pt" : "resume_summary_en") ?? "";
        var prompt = $"""
Write a concise, specific cover letter in {languageName} for this job application.
Use only the candidate facts provided below. Never invent experience, qualifications, results, employers, dates, or skills. If facts are sparse, keep the letter brief and make no unsupported claims. Do not claim the candidate already applied. Return only the letter, with no subject line or commentary.

{ExperiencePriorityRule}

Candidate name: {(name.Length > 0 ? name : "[candidate name]")}
Candidate-provided facts:
{(facts.Length > 0 ? facts : "[No candidate facts provided.]")}
{bridgeBlock}Resume analysis summary (authoritative; do not invent beyond this):
{(summary.Length > 0 ? summary : "[No resume analysis provided.]")}

Job title: {job.Title}
Company: {job.Company}
Location / eligibility: {job.Location}
Job description:
{Text.Take(Text.StripTags(job.Description), 10000)}

""";
        if (provider is not ("openai" or "gemini")) throw new TriageValidationException("Provedor de IA deve ser Gemini ou OpenAI.");
        var letter = await ai.TextAsync(prompt, new AiCall(provider, model, _secret(provider)), maxOutputTokens: 1400, temperature: 0.4, ct: ct);
        if (string.IsNullOrEmpty(letter)) throw new InvalidOperationException("A API não retornou uma carta de apresentação.");
        return letter;
    }

    /// <summary>Gera e grava a carta (substitui a anterior); a vaga "new"/"review" passa a "prepared". Devolve o id da carta.</summary>
    public async Task<long> CreateCoverLetterAsync(long jobId, string? resumeLanguage = null, IReadOnlyList<string>? bridges = null, CancellationToken ct = default)
    {
        var cfg = new SettingsStore(db).All();
        JobRow? job;
        Dictionary<string, string> summaries;
        using (var connection = db.Open())
        {
            job = connection.QueryFirstOrDefault<JobRow>($"SELECT {JobColumns} FROM jobs WHERE id=@jobId", new { jobId });
            summaries = ResumeData.Summaries(connection);
        }
        if (job is null) throw new TriageValidationException("Vaga não encontrada.");
        var language = !string.IsNullOrEmpty(resumeLanguage) ? resumeLanguage : job.Language ?? "";
        if (language is not ("pt" or "en")) throw new TriageValidationException("Idioma da vaga incerto. Corrija o idioma antes de gerar a carta.");
        cfg["resume_summary_pt"] = summaries.GetValueOrDefault("pt", "");
        cfg["resume_summary_en"] = summaries.GetValueOrDefault("en", "");
        var (provider, model) = ProviderAndModel(cfg);
        var letter = await GenerateLetterAsync(provider, model, language, ToAssess(job), cfg, bridges, ct);
        var stamp = Clock.NowIso();
        long id;
        using (var connection = db.Open())
        using (var tx = connection.BeginTransaction())
        {
            connection.Execute(
                @"INSERT INTO cover_letters(job_id,language,provider,model,body,created_at,updated_at) VALUES(@jobId,@language,@provider,@model,@letter,@stamp,@stamp)
                  ON CONFLICT(job_id) DO UPDATE SET language=excluded.language,provider=excluded.provider,model=excluded.model,body=excluded.body,updated_at=excluded.updated_at",
                new { jobId, language, provider, model, letter, stamp }, tx);
            connection.Execute("UPDATE jobs SET status='prepared' WHERE id=@jobId AND status IN ('new','review')", new { jobId }, tx);
            id = connection.ExecuteScalar<long>("SELECT id FROM cover_letters WHERE job_id=@jobId", new { jobId }, tx);
            tx.Commit();
        }
        db.Bus.Publish(new JobsChanged([jobId]));
        return id;
    }

    // ------------------------------------------------------------ movimentos da vaga

    private void Execute(string sql, object args, long jobId)
    {
        using (var connection = db.Open()) connection.Execute(sql, args);
        db.Bus.Publish(new JobsChanged([jobId]));
    }

    public void MarkWorthLooking(long jobId, int score, string reason, string detail)
    {
        var note = Text.Take($"Vale a pena olhar (score {score}/100). {detail} Motivo da IA: {reason}", 900);
        using (var connection = db.Open())
        using (var tx = connection.BeginTransaction())
        {
            connection.Execute("UPDATE jobs SET status='worth', notes=@note WHERE id=@jobId", new { note, jobId }, tx);
            connection.Execute("UPDATE ai_decisions SET apply_channel='manual', apply_result=@result WHERE id=(SELECT MAX(id) FROM ai_decisions WHERE job_id=@jobId)",
                new { result = Text.Take(detail, 500), jobId }, tx);
            tx.Commit();
        }
        db.Bus.Publish(new JobsChanged([jobId]));
        _log.Add("info", "worth", $"Vaga #{jobId} enviada para 'Vale a pena olhar' — {detail}");
    }

    private void RecordBlocked(long jobId, string detail, long resumeId, long coverLetterId)
    {
        using var connection = db.Open();
        connection.Execute(
            "INSERT INTO applications(job_id,channel,recipient,resume_id,cover_letter_id,status,detail,attempted_at) VALUES(@jobId,'none','',@resumeId,@coverLetterId,'blocked',@detail,@now)",
            new { jobId, resumeId, coverLetterId, detail = Text.Take(detail, 500), now = Clock.NowIso() });
    }

    // ------------------------------------------------------------ triagem completa

    /// <summary>Triagem com análise do currículo; a candidatura automática fica limitada a preparar a carta e mover a vaga.</summary>
    public async Task<string> ProcessAutoJobAsync(long jobId, Action<string>? progress = null, CancellationToken ct = default)
    {
        var say = progress ?? (_ => { });
        var cfg = new SettingsStore(db).All();
        JobRow? row;
        using (var connection = db.Open())
            row = connection.QueryFirstOrDefault<JobRow>($"SELECT {JobColumns} FROM jobs WHERE id=@jobId", new { jobId });
        if (row is null)
        {
            say($"Vaga #{jobId} não existe mais; nada a fazer");
            return "vaga removida";
        }
        var title = row.Title ?? "";
        say($"Vaga #{jobId}: {title} — {(string.IsNullOrEmpty(row.Company) ? "empresa n/d" : row.Company)} ({(string.IsNullOrEmpty(row.Location) ? "local n/d" : row.Location)})");
        _log.Add("info", "auto-apply", $"Triagem da vaga #{jobId}: {title}");
        var excluded = RegionFilter.ExcludedReason(row.Location, row.Title, row.Description);
        if (excluded.Length > 0) // sem chamar a IA: vagas da Índia já coletadas antes do filtro também saem da fila
        {
            Execute("UPDATE jobs SET status='ignored', notes=@note WHERE id=@jobId", new { note = $"Região excluída: {excluded}.", jobId }, jobId);
            _log.Add("info", "auto-apply", $"Vaga #{jobId} ignorada sem triagem: {excluded}.");
            say($"Ignorada sem chamar a IA — região excluída: {excluded}");
            return $"vaga {jobId}: região excluída";
        }

        TriageDecision decision;
        try
        {
            decision = await AssessAsync(ToAssess(row), cfg, JobTrack(row.SearchFilterId), progress: say, ct: ct);
        }
        catch (AiUnavailableException ex)
        {
            say($"IA indisponível: {ex.Message}"); // segue para a fila repetir com backoff
            throw;
        }
        catch (TriageValidationException ex)
        {
            Execute("UPDATE jobs SET status='blocked', notes=@note WHERE id=@jobId", new { note = ex.Message, jobId }, jobId);
            _log.Add("warning", "auto-apply", $"Vaga #{jobId}: {ex.Message}");
            say($"Bloqueada antes da IA: {ex.Message}");
            return $"vaga {jobId}: {ex.Message}";
        }

        // A aprovação já aplica o mínimo da trilha (core/transferível), bloqueios e revisão cética.
        var outcome = decision.Outcome;
        var qualifies = outcome.ShouldApply;
        var resumeLanguage = decision.ResumeLanguage;
        using (var connection = db.Open())
            connection.Execute(
                @"INSERT INTO ai_decisions(job_id,decided_at,match_score,should_apply,letter_required,reason,resume_language,apply_channel,apply_result,assessment_json,scoring_version,provider,model)
                  VALUES(@jobId,@now,@score,@apply,@letter,@reason,@resumeLanguage,'','',@assessment,@version,@provider,@model)",
                new
                {
                    jobId, now = Clock.NowIso(), score = outcome.MatchScore, apply = qualifies ? 1 : 0, letter = outcome.LetterRequired ? 1 : 0, reason = outcome.Reason,
                    resumeLanguage, assessment = outcome.Assessment.ToJsonString(Json.Options), version = decision.ScoringVersion, provider = decision.Provider, model = decision.Model,
                });
        db.Bus.Publish(new JobsChanged([jobId]));
        _log.Add("info", "auto-apply",
            $"Vaga #{jobId}: score {outcome.MatchScore}/100, aprovada={(qualifies ? "True" : "False")}, trilha={outcome.Track}, CV={resumeLanguage}. {outcome.Reason}");
        say($"Decisão: {(qualifies ? "APROVADA" : "DESCARTADA")} · score {outcome.MatchScore}/100 · trilha {outcome.Track} · CV {resumeLanguage} · modelo {decision.Provider}/{decision.Model}");
        say($"Raciocínio da IA: {outcome.Reason}");
        if (!qualifies)
        {
            Execute("UPDATE jobs SET status='ignored', notes=@note WHERE id=@jobId", new { note = $"IA: score {outcome.MatchScore}/100. {outcome.Reason}", jobId }, jobId);
            _log.Add("info", "auto-apply", $"Vaga #{jobId} ignorada (bloqueio, cobertura ou score abaixo do mínimo).");
            say("Vaga movida para descartadas (bloqueio, cobertura ou score abaixo do mínimo)");
            return $"IA descartou vaga {jobId} ({outcome.MatchScore}/100)";
        }

        ResumeRecord? resume;
        using (var connection = db.Open()) resume = ResumeData.Get(connection, resumeLanguage);
        if (resume is null || resume.AnalysisSummary.Trim().Length == 0)
        {
            Execute("UPDATE jobs SET status='blocked', notes=@note WHERE id=@jobId", new { note = $"Sem currículo analisado em {resumeLanguage}.", jobId }, jobId);
            say($"Bloqueada: currículo {resumeLanguage} sem análise");
            return $"vaga {jobId}: currículo {resumeLanguage} ausente";
        }

        say($"Gerando carta de apresentação ({resumeLanguage})");
        long coverId;
        try
        {
            coverId = await CreateCoverLetterAsync(jobId, resumeLanguage, outcome.Bridges, ct);
        }
        catch (AiUnavailableException ex)
        {
            say($"IA indisponível ao gerar a carta: {ex.Message}");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Execute("UPDATE jobs SET status='blocked', notes=@note WHERE id=@jobId", new { note = $"Falha ao gerar carta: {ex.Message}", jobId }, jobId);
            say($"Falha ao gerar a carta: {ex.Message}");
            return $"vaga {jobId}: falha na carta";
        }

        var score = outcome.MatchScore;
        say("Carta de apresentação pronta");

        // Candidaturas via página web ficam para o usuário no Chrome com a extensão.
        if (IsLinkedInJob(row.Url, row.Source, row.Description, row.Company))
        {
            say("Vaga do LinkedIn: movida para 'Vale a pena olhar' (candidatura manual pela extensão)");
            MarkWorthLooking(jobId, score, outcome.Reason, "Vaga do LinkedIn — carta preparada. Abra no Chrome e use Odradek Autofill para preencher manualmente.");
            return $"vaga {jobId}: vale a pena olhar (LinkedIn, score {score})";
        }

        // A IA nunca envia e-mail sozinha: com contato detectado, a vaga espera a confirmação do usuário no card.
        var recipients = Emails.JobContacts(row.Description, row.Url, row.Company);
        if (recipients.Count == 0)
        {
            const string detail = "Nenhum e-mail de contato encontrado na vaga.";
            say($"{detail} Movida para 'Vale a pena olhar' (candidatura manual pela extensão)");
            RecordBlocked(jobId, detail, resume.Id, coverId);
            MarkWorthLooking(jobId, score, outcome.Reason, $"Envio por e-mail indisponível ({detail}) Abra a vaga no Chrome e use Odradek Autofill.");
            return $"vaga {jobId}: vale a pena olhar — {detail}";
        }
        var shown = string.Join(", ", recipients.Take(3));
        say($"E-mail de contato detectado ({shown}). Nada foi enviado: aguardando sua confirmação no card da vaga");
        MarkWorthLooking(jobId, score, outcome.Reason,
            $"E-mail de contato detectado ({shown}). Revise a carta e confirme o envio no card da vaga; nada foi enviado.");
        return $"vaga {jobId}: vale a pena olhar — e-mail de contato detectado, aguardando sua confirmação (score {score})";
    }

    /// <summary>Handler do tipo <c>job_apply</c> da fila.</summary>
    public QueueHandler ApplyHandler => async (job, ct) =>
    {
        var jobId = (long)JobAssessment.Number(job.Payload["job_id"]);
        if (jobId == 0) throw new NonRetryableException("job_id ausente no payload da fila.");
        return await ProcessAutoJobAsync(jobId, job.Progress, ct);
    };
}
