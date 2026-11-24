using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Odradek.Core.Data;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Odradek.Core.Actions;

/// <summary>
/// Envio e reanálise de currículos (<c>candidate/resume_pipeline.py:persist_resume_upload</c>, <c>resume_storage.py</c> e a rota
/// <c>/api/resumes/*</c>). A análise pela IA em si é da etapa 4: aqui o PDF é guardado no banco e o item entra na fila.
/// </summary>
public sealed partial class ResumeActions(Database db)
{
    public const int MaxPdfBytes = 12 * 1024 * 1024;

    private sealed class ResumeRow
    {
        public long Id { get; set; }
        public string Language { get; set; } = "";
        public string OriginalFilename { get; set; } = "";
        public string StoredPath { get; set; } = "";
        public string FileSha256 { get; set; } = "";
        public string ExtractedText { get; set; } = "";
        public string AnalysisJson { get; set; } = "";
        public string AnalysisSummary { get; set; } = "";
        public string? AnalyzedAt { get; set; }
        public string ProfileJson { get; set; } = "";
        public string ProfileVersion { get; set; } = "";
    }

    [GeneratedRegex(@" {2,}")] private static partial Regex Spaces();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"[A-Za-zÀ-ÿ]-$")] private static partial Regex EndsHyphenated();
    [GeneratedRegex(@"^[a-zà-ÿ]")] private static partial Regex StartsLower();

    /// <summary>Preserva linhas e seções; remove cabeçalhos/rodapés repetidos e junta hifenização (<c>_clean_pdf_pages</c>).</summary>
    public static string CleanPages(IReadOnlyList<string> pages)
    {
        var pageLines = pages.Select(page => Text.SplitLines(page ?? "")
            .Select(line => Spaces().Replace(line.Replace("\t", " ").Replace(" ", " "), " ").Trim()).ToList()).ToList();
        var repeated = new HashSet<string>();
        if (pageLines.Count >= 3)
        {
            var counts = new Dictionary<string, int>();
            foreach (var lines in pageLines)
                foreach (var line in lines.Where(l => l.Length > 0 && l.Length < 80).Select(l => l.ToLowerInvariant()).Distinct())
                    counts[line] = counts.GetValueOrDefault(line) + 1;
            var threshold = Math.Max(3, (int)(pageLines.Count * 0.6 + 0.999));
            repeated = counts.Where(kv => kv.Value >= threshold).Select(kv => kv.Key).ToHashSet();
        }
        var merged = new List<string>();
        foreach (var lines in pageLines)
        {
            foreach (var line in lines)
            {
                if (line.Length > 0 && repeated.Contains(line.ToLowerInvariant())) continue;
                if (line.Length > 0 && merged.Count > 0 && EndsHyphenated().IsMatch(merged[^1]) && StartsLower().IsMatch(line))
                {
                    merged[^1] = merged[^1][..^1] + line;
                    continue;
                }
                if (line.Length == 0 && (merged.Count == 0 || merged[^1].Length == 0)) continue;
                merged.Add(line);
            }
            if (merged.Count > 0 && merged[^1].Length > 0) merged.Add("");
        }
        return string.Join('\n', merged).Trim();
    }

    /// <summary>Texto do PDF (até 50 000 caracteres); lança erro se não houver texto útil (PDF só de imagem).</summary>
    public static string ExtractText(byte[] pdf)
    {
        string cleaned;
        try
        {
            using var document = PdfDocument.Open(pdf);
            cleaned = CleanPages(document.GetPages().Select(page =>
            {
                var ordered = ContentOrderTextExtractor.GetText(page);
                return string.IsNullOrWhiteSpace(ordered) ? page.Text : ordered;
            }).ToList());
        }
        catch (Exception ex) when (ex is not ActionException)
        {
            throw new ActionException("Envie um arquivo PDF válido.");
        }
        if (Whitespace().Replace(cleaned, " ").Length < 40)
            throw new ActionException("Não foi possível extrair texto útil deste PDF. Use um PDF com texto selecionável.");
        return Text.Take(cleaned, 50000);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ResumeRow? Get(SqliteConnection connection, string language, SqliteTransaction? tx = null) =>
        connection.QueryFirstOrDefault<ResumeRow>(
            @"SELECT id, language, original_filename, stored_path, file_sha256, extracted_text, analysis_json, analysis_summary, analyzed_at,
                     COALESCE(profile_json,'') AS profile_json, COALESCE(profile_version,'') AS profile_version
              FROM resumes WHERE language=@language", new { language }, tx);

    private static void SavePdf(SqliteConnection connection, SqliteTransaction tx, ResumeRow resume, byte[] content)
    {
        if (content.Length is 0 or > MaxPdfBytes || content[0] != '%' || Sha256(content) != resume.FileSha256)
            throw new ActionException("Arquivo de currículo inválido ou maior que 12 MB.");
        connection.Execute("INSERT INTO resume_files(resume_id,content) VALUES(@id,@content) ON CONFLICT(resume_id) DO UPDATE SET content=excluded.content",
            new { id = resume.Id, content }, tx);
    }

    /// <summary>O PDF guardado do currículo, validado contra o SHA-256 cadastrado; <c>null</c> se não existir no banco nem no disco.</summary>
    public static byte[]? ReadPdf(SqliteConnection connection, string language)
    {
        var resume = Get(connection, language);
        if (resume is null) return null;
        var stored = connection.QueryFirstOrDefault<byte[]?>("SELECT content FROM resume_files WHERE resume_id=@id", new { id = resume.Id });
        if (stored is { Length: > 0 } && Sha256(stored) == resume.FileSha256) return stored;
        var root = Path.GetFullPath(Paths.ResumesDir);
        foreach (var candidate in new[] { resume.StoredPath, Path.Combine(root, $"resume_{language}.pdf") })
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || new FileInfo(full).Length > MaxPdfBytes) continue;
                var bytes = File.ReadAllBytes(full);
                if (Sha256(bytes) == resume.FileSha256) return bytes;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Guarda o PDF e enfileira a análise. Devolve a mensagem para a tela.</summary>
    public string Upload(string language, string filename, byte[] raw, bool force)
    {
        language = (language ?? "").Trim().ToLowerInvariant();
        if (language is not ("pt" or "en")) throw new ActionException("Idioma do currículo deve ser pt ou en.");
        if (raw.Length < 4 || raw[0] != '%' || raw[1] != 'P' || raw[2] != 'D' || raw[3] != 'F') throw new ActionException("Envie um arquivo PDF válido.");
        if (raw.Length > MaxPdfBytes) throw new ActionException("PDF maior que 12 MB.");

        var stamp = Clock.NowIso();
        var storedPath = Path.Combine(Paths.ResumesDir, $"resume_{language}.pdf");
        var digest = Sha256(raw);
        string message;
        bool needsAnalysis;
        using (var connection = db.Open())
        {
            var existing = Get(connection, language);
            var sameAnalyzed = existing is not null && existing.FileSha256 == digest && existing.AnalysisSummary.Trim().Length > 0 && !force;
            using var tx = connection.BeginTransaction();
            if (sameAnalyzed)
            {
                message = $"Currículo {language.ToUpperInvariant()}: PDF idêntico ao já analisado. Análise reutilizada. Use «Reanalisar currículo» ou marque «Forçar nova análise».";
                connection.Execute(
                    @"UPDATE resumes SET original_filename=@filename, stored_path=@storedPath, analysis_status='reused', analysis_error='', analysis_message=@message, updated_at=@stamp WHERE language=@language",
                    new { filename, storedPath, message, stamp, language }, tx);
                SavePdf(connection, tx, Get(connection, language, tx)!, raw);
                needsAnalysis = false;
            }
            else
            {
                var extracted = ExtractText(raw);
                message = force && existing is not null && existing.FileSha256 == digest
                    ? $"Currículo {language.ToUpperInvariant()}: mesmo PDF, mas reanálise forçada foi enfileirada."
                    : $"Currículo {language.ToUpperInvariant()}: PDF salvo. Análise da IA enfileirada (será retentada automaticamente se a API estiver ocupada).";
                connection.Execute(
                    @"INSERT INTO resumes(language,original_filename,stored_path,file_sha256,extracted_text,analysis_json,analysis_summary,analyzed_at,provider,model,
                                          analysis_status,analysis_error,analysis_message,created_at,updated_at,profile_json,profile_version)
                      VALUES(@language,@filename,@storedPath,@digest,@extracted,@analysisJson,@summary,@analyzedAt,'','','pending','',@message,@stamp,@stamp,@profileJson,@profileVersion)
                      ON CONFLICT(language) DO UPDATE SET original_filename=excluded.original_filename, stored_path=excluded.stored_path, file_sha256=excluded.file_sha256,
                        extracted_text=excluded.extracted_text, analysis_json=excluded.analysis_json, analysis_summary=excluded.analysis_summary, analyzed_at=excluded.analyzed_at,
                        provider=excluded.provider, model=excluded.model, analysis_status=excluded.analysis_status, analysis_error=excluded.analysis_error,
                        analysis_message=excluded.analysis_message, profile_json=excluded.profile_json, profile_version=excluded.profile_version, updated_at=excluded.updated_at",
                    new
                    {
                        language, filename, storedPath, digest, extracted, analysisJson = existing?.AnalysisJson ?? "", summary = existing?.AnalysisSummary ?? "",
                        analyzedAt = existing?.AnalyzedAt, message = Text.Take(message, 500), stamp, profileJson = existing?.ProfileJson ?? "", profileVersion = existing?.ProfileVersion ?? "",
                    }, tx);
                SavePdf(connection, tx, Get(connection, language, tx)!, raw);
                needsAnalysis = true;
            }
            tx.Commit();
        }
        if (needsAnalysis)
        {
            var queueId = new QueueActions(db).Enqueue(QueueActions.KindResume,
                [new("language", language), new("force", true), new("requested_at", Clock.NowIso())], force ? null : $"resume:{language}");
            message = $"{message} Job de fila #{queueId}.";
        }
        new EventLogStore(db).Add("info", "resume", message);
        db.Bus.Publish(new ResumesChanged());
        return message;
    }

    /// <summary>Marca como pendente, cancela análises ativas do idioma e enfileira uma nova (<c>_reanalyze_resume</c>).</summary>
    public string Reanalyze(string language)
    {
        language = (language ?? "").Trim().ToLowerInvariant();
        if (language is not ("pt" or "en")) throw new ActionException("Idioma inválido para reanálise.");
        List<long> actives;
        using (var connection = db.Open())
        {
            var row = Get(connection, language) ?? throw new ActionException($"Não há currículo {language.ToUpperInvariant()} salvo.");
            if (row.ExtractedText.Trim().Length == 0 && ReadPdf(connection, language) is null)
                throw new ActionException($"Currículo {language.ToUpperInvariant()} sem arquivo/texto para reanalisar.");
            connection.Execute(
                "UPDATE resumes SET analysis_status='pending', analysis_error='', analysis_message=@message, updated_at=@stamp WHERE language=@language",
                new { message = $"Currículo {language.ToUpperInvariant()}: reanálise forçada na fila.", stamp = Clock.NowIso(), language });
            actives = connection.Query<long>(
                "SELECT id FROM queue_jobs WHERE kind=@kind AND status IN ('pending','running','retry_wait') AND payload LIKE @like",
                new { kind = QueueActions.KindResume, like = $"%\"language\": \"{language}\"%" }).ToList();
        }
        var queue = new QueueActions(db);
        foreach (var id in actives) queue.Cancel(id);
        var queueId = queue.Enqueue(QueueActions.KindResume, [new("language", language), new("force", true), new("requested_at", Clock.NowIso())]);
        var message = $"Reanálise do currículo {language.ToUpperInvariant()} enfileirada (job #{queueId}). Acompanhe em Filas/Logs.";
        new EventLogStore(db).Add("info", "resume", message);
        db.Bus.Publish(new ResumesChanged());
        return message;
    }
}
