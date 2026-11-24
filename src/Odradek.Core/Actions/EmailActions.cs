using Dapper;
using Odradek.Core.Data;

namespace Odradek.Core.Actions;

/// <summary>Teste de SMTP e envio de candidatura por e-mail confirmado pelo usuário (<c>panel_settings.send_smtp_test</c>, <c>app.send_application_email</c>).</summary>
public sealed class EmailActions(Database db)
{
    private readonly EventLogStore _log = new(db);

    public async Task<ActionResult> SendTestAsync()
    {
        var cfg = new SettingsStore(db).All();
        var to = new[] { cfg.GetValueOrDefault("candidate_email"), cfg.GetValueOrDefault("smtp_from") }.Select(s => (s ?? "").Trim()).FirstOrDefault(s => s.Length > 0) ?? "";
        if (to.Length == 0) return new(false, "Configure o e-mail do candidato ou remetente para testar.");
        try
        {
            await Smtp.SendAsync(Smtp.ConfigFrom(cfg, SecretStore.Get("smtp_password")), [to], "Odradek Scraper — teste SMTP", "Este é um e-mail de teste do Odradek Scraper.");
            _log.Add("success", "smtp", $"E-mail de teste enviado para {to}.");
            return new(true, $"E-mail de teste enviado para {to}.");
        }
        catch (Exception ex)
        {
            _log.Add("error", "smtp", $"Falha no teste SMTP: {ex.Message}");
            return new(false, $"Falha no teste SMTP: {ex.Message}");
        }
    }

    private sealed class JobRow
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string Company { get; set; } = "";
        public string Url { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Language { get; set; }
    }

    /// <summary>Só aceita endereços detectados na própria vaga (até 3), exige carta e currículo analisado; marca a vaga como aplicada ao enviar.</summary>
    public async Task<string> SendApplicationAsync(long jobId, IReadOnlyList<string> recipients)
    {
        if (recipients.Count > 10) throw new ActionException("Destinatários inválidos");
        var cfg = new SettingsStore(db).All();
        JobRow job; long letterId; string letterBody; long resumeId; string resumeLanguage, resumeSha;
        byte[] resumePdf;
        List<string> chosen;
        using (var connection = db.Open())
        {
            job = connection.QueryFirstOrDefault<JobRow>("SELECT id, title, company, url, description, status, language FROM jobs WHERE id=@jobId", new { jobId })
                  ?? throw new ActionException("Vaga não encontrada.");
            if (job.Status == "applied") throw new ActionException("Esta vaga já está marcada como aplicada.");
            var letter = connection.QueryFirstOrDefault<(long Id, string Body)?>("SELECT id, body FROM cover_letters WHERE job_id=@jobId", new { jobId });
            var decisionLanguage = connection.QueryFirstOrDefault<string?>("SELECT resume_language FROM ai_decisions WHERE job_id=@jobId ORDER BY id DESC LIMIT 1", new { jobId });
            var language = !string.IsNullOrEmpty(decisionLanguage) ? decisionLanguage : (!string.IsNullOrEmpty(job.Language) ? job.Language : "pt");
            var resume = connection.QueryFirstOrDefault<(long Id, string Language, string Sha, string Summary)?>(
                "SELECT id, language, file_sha256 AS Sha, analysis_summary AS Summary FROM resumes WHERE language=@language", new { language })
                ?? connection.QueryFirstOrDefault<(long Id, string Language, string Sha, string Summary)?>(
                    "SELECT id, language, file_sha256 AS Sha, analysis_summary AS Summary FROM resumes WHERE language=@other", new { other = language == "pt" ? "en" : "pt" });
            var detected = Emails.JobContacts(job.Description, job.Url, job.Company).ToDictionary(e => e.ToLowerInvariant(), e => e);
            chosen = recipients.Select(r => r.Trim().ToLowerInvariant()).Where(detected.ContainsKey).Select(r => detected[r]).Distinct().Take(3).ToList();
            if (chosen.Count == 0) throw new ActionException("Escolha ao menos um e-mail de contato detectado na vaga.");
            if (letter is null || letter.Value.Body.Trim().Length == 0) throw new ActionException("A vaga não tem carta de apresentação; gere a carta antes de enviar.");
            if (resume is null || resume.Value.Summary.Trim().Length == 0) throw new ActionException("Nenhum currículo analisado para anexar.");
            letterId = letter.Value.Id; letterBody = letter.Value.Body;
            resumeId = resume.Value.Id; resumeLanguage = resume.Value.Language; resumeSha = resume.Value.Sha;
            resumePdf = ResumeActions.ReadPdf(connection, resumeLanguage)
                        ?? throw new ActionException("Arquivo de currículo indisponível. Reenvie o PDF neste idioma para armazená-lo no banco.");
        }
        if ((cfg.GetValueOrDefault("smtp_host") ?? "").Trim().Length == 0) throw new ActionException("Falha ao enviar o e-mail: SMTP não configurado.");
        var recipientCsv = string.Join(',', chosen);
        var candidate = cfg.GetValueOrDefault("candidate_name");
        var subject = $"Candidatura: {job.Title} — {(string.IsNullOrEmpty(candidate) ? "candidato" : candidate)}";
        try
        {
            await Smtp.SendAsync(Smtp.ConfigFrom(cfg, SecretStore.Get("smtp_password")), chosen, subject, letterBody, resumePdf, $"resume_{resumeLanguage}_{resumeSha}.pdf");
        }
        catch (Exception ex)
        {
            RecordApplication(jobId, recipientCsv, resumeId, letterId, "failed", Text.Take(ex.Message, 500));
            _log.Add("error", "email", $"Vaga #{jobId}: envio por e-mail falhou: Falha SMTP: {ex.Message}");
            throw new ActionException($"Falha ao enviar o e-mail: Falha SMTP: {ex.Message}");
        }
        RecordApplication(jobId, recipientCsv, resumeId, letterId, "sent", "E-mail enviado");
        var detail = $"E-mail enviado para {recipientCsv}";
        using (var connection = db.Open())
        {
            connection.Execute("UPDATE jobs SET status='applied', applied_at=@now, notes=@notes WHERE id=@jobId", new { now = Clock.NowIso(), notes = Text.Take(detail, 900), jobId });
            connection.Execute("UPDATE ai_decisions SET apply_channel='email', apply_result=@result WHERE id=(SELECT MAX(id) FROM ai_decisions WHERE job_id=@jobId)",
                new { result = Text.Take(detail, 500), jobId });
        }
        db.Bus.Publish(new JobsChanged([jobId]));
        _log.Add("success", "email", $"Vaga #{jobId} aplicada via e-mail (confirmado no painel): {detail}");
        return detail;
    }

    private void RecordApplication(long jobId, string recipient, long resumeId, long letterId, string status, string detail)
    {
        using var connection = db.Open();
        connection.Execute(
            "INSERT INTO applications(job_id,channel,recipient,resume_id,cover_letter_id,status,detail,attempted_at) VALUES(@jobId,'email',@recipient,@resumeId,@letterId,@status,@detail,@now)",
            new { jobId, recipient, resumeId, letterId, status, detail, now = Clock.NowIso() });
    }
}
