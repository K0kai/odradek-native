using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Candidate;
using Odradek.Core.Data;
using Odradek.Core.Queue;

namespace Odradek.Core.Engines;

/// <summary>Análise do currículo já guardado (<c>run_resume_analysis</c>, <c>mark_resume_analysis_error</c> e <c>handle_resume_analysis_job</c>).</summary>
public sealed class ResumeAnalyzer(Database db, AiClient ai, Func<string, string>? secret = null)
{
    private readonly Func<string, string> _secret = secret ?? (name => SecretStore.Get(name));

    /// <summary>Executa a análise de IA do currículo persistido e grava o resultado. Pode lançar <see cref="AiUnavailableException"/>.</summary>
    public async Task<string> RunAsync(string language, string provider, string model, string apiKey, string nowIso,
        ResumeAnalysis.ProfileCall? call = null, DateOnly? today = null, CancellationToken ct = default)
    {
        ResumeRecord? row;
        string text;
        using (var connection = db.Open())
        {
            row = ResumeData.Get(connection, language) ?? throw new InvalidOperationException($"Nenhum currículo {language} salvo para analisar.");
            text = row.ExtractedText.Trim();
            if (text.Length == 0)
            {
                var pdf = ResumeActions.ReadPdf(connection, language) ?? throw new InvalidOperationException("Arquivo de currículo indisponível. Reenvie o PDF neste idioma.");
                text = ResumeActions.ExtractText(pdf);
            }
        }
        var analysis = await ResumeAnalysis.AnalyzeAsync(text, language, provider, model, apiKey, ai, call, today, ct);
        var profile = analysis["_profile"] as JsonObject;
        analysis.Remove("_profile");
        var summary = Text.PyStr(analysis["summary"]).Trim();
        var message = $"Currículo {language.ToUpperInvariant()}: análise da IA concluída ({provider}/{model}).";
        using (var connection = db.Open())
            connection.Execute(
                @"UPDATE resumes SET extracted_text=@text, analysis_json=@analysis, analysis_summary=@summary, analyzed_at=@now, provider=@provider, model=@model,
                         analysis_status='ok', analysis_error='', analysis_message=@message, profile_json=@profileJson, profile_version=@profileVersion, updated_at=@now
                  WHERE language=@language",
                new
                {
                    text, analysis = analysis.ToJsonString(Json.Options), summary, now = nowIso, provider, model, message, language,
                    profileJson = profile is null ? "" : profile.ToJsonString(Json.Options), profileVersion = profile is null ? "" : Text.PyStr(profile["version"]),
                });
        db.Bus.Publish(new ResumesChanged());
        return message;
    }

    public void MarkError(string language, string error, string nowIso)
    {
        using (var connection = db.Open())
        {
            if (ResumeData.Get(connection, language) is null) return;
            connection.Execute("UPDATE resumes SET analysis_status='error', analysis_error=@error, analysis_message=@message, updated_at=@now WHERE language=@language",
                new { error = Text.Take(error, 1000), message = Text.Take($"Currículo {language.ToUpperInvariant()}: falha na análise — {error}", 500), now = nowIso, language });
        }
        db.Bus.Publish(new ResumesChanged());
    }

    /// <summary>Handler do tipo <c>resume_analysis</c> da fila.</summary>
    public QueueHandler Handler => async (job, ct) =>
    {
        var language = Text.PyStr(job.Payload["language"]).ToLowerInvariant();
        var cfg = new SettingsStore(db).All();
        var provider = (cfg.GetValueOrDefault("ai_provider") ?? "gemini").ToLowerInvariant();
        var model = (cfg.GetValueOrDefault("ai_model") ?? "gemini-3.5-flash-lite").Trim();
        var stamp = Clock.NowIso();
        try
        {
            return await RunAsync(language, provider, model, _secret(provider), stamp, ct: ct);
        }
        catch (AiUnavailableException)
        {
            throw; // a fila repete com backoff; o currículo continua "pending"
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkError(language, $"{ex.GetType().Name.Replace("Exception", "Error")}: {ex.Message}", stamp);
            throw;
        }
    };
}
