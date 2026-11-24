using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Queue;

namespace Odradek.Core.Engines;

public sealed record ReverifySummary(int Checked, int Ignored, int Active, int Unknown, bool Aborted);

/// <summary>
/// Verificação diária das vagas "vale a pena" sem navegador (<c>jobs/worth_reverify.py</c>): só evidência clara de vaga encerrada
/// move a vaga para "ignorada"; qualquer dúvida mantém.
/// </summary>
public sealed partial class WorthReverify(Database db, HttpClient http, double httpTimeoutSeconds = 12.0)
{
    public const string Inactive = "inactive";
    public const string Active = "active";
    public const string Unknown = "unknown";
    public const string UserAgent = "JobScraper/1.0 (public job availability check)";

    private static readonly string[] GuestApplyMarkers = ["public_jobs_apply-link-onsite", "public_jobs_apply-link-offsite", "job-details-topcard-apply-modal", "topcard-apply"];

    [GeneratedRegex(@"apply\s+now|candidatar(-se)?|enviar\s+candidatura|submit\s+application|apply\s+for\s+this\s+(job|role)|finalizar\s+candidatura", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveText();

    [GeneratedRegex(@"linkedin\.com/(?:[\w.-]+/)?jobs/view/(?:[\w%-]*?-)?(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkedInJobId();

    public static string? ExtractLinkedInJobId(string? url)
    {
        var raw = (url ?? "").Trim();
        var match = LinkedInJobId().Match(raw);
        if (match.Success) return match.Groups[1].Value;
        var queryStart = raw.IndexOf('?');
        if (queryStart < 0) return null;
        var query = raw[(queryStart + 1)..];
        var hash = query.IndexOf('#');
        if (hash >= 0) query = query[..hash];
        var values = query.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).Select(p => (Key: Uri.UnescapeDataString(p[0].Replace('+', ' ')), Value: Uri.UnescapeDataString(p[1].Replace('+', ' ')))).ToList();
        foreach (var key in new[] { "currentJobId", "jobId", "trkJobId" })
            if (values.FirstOrDefault(v => v.Key == key) is { Key: not null } found && found.Value.Length > 0 && found.Value.All(char.IsAsciiDigit)) return found.Value;
        return null;
    }

    public static string ClassifyHttp(int status, string? body)
    {
        if (status is 404 or 410 or 451) return Inactive;
        if (status >= 500 || status is 0 or 401 or 403) return Unknown;
        if (ApifyItems.TextIndicatesClosed(body)) return Inactive;
        return ActiveText().IsMatch(body ?? "") ? Active : Unknown;
    }

    /// <summary>GET público com tempo limite: (status, corpo). Qualquer falha de rede vira <c>(0, "")</c>.</summary>
    public async Task<(int Status, string Body)> FetchUrlTextAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            request.Headers.TryAddWithoutValidation("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(httpTimeoutSeconds));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var limit = response.IsSuccessStatusCode ? 300_000 : 100_000;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[limit];
            var read = 0;
            while (read < limit)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, limit - read), timeout.Token).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet ?? "utf-8"); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            return (status, encoding.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (0, "");
        }
    }

    /// <summary>Endpoint público (sem login) que o LinkedIn usa para exibir a vaga a visitantes.</summary>
    public async Task<(string Verdict, string Detail)> CheckLinkedInGuestAsync(string jobId, CancellationToken ct = default)
    {
        var id = (jobId ?? "").Trim();
        if (id.Length == 0 || !id.All(char.IsAsciiDigit)) return (Unknown, "ID público da vaga indisponível");
        var (status, body) = await FetchUrlTextAsync($"https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{id}", ct).ConfigureAwait(false);
        if (status == 0) return (Unknown, "endpoint público indisponível");
        var verdict = ClassifyHttp(status, body);
        if (verdict == Inactive) return (Inactive, $"endpoint público HTTP {status} indica vaga encerrada");
        if (GuestApplyMarkers.Any(body.Contains)) return (Active, "endpoint público mostra candidatura disponível");
        if (verdict == Active) return (Active, "endpoint público mostra sinal de candidatura");
        return (Unknown, $"endpoint público HTTP {status} sem sinal conclusivo");
    }

    public async Task<(string Verdict, string Detail)> CheckJobAsync(string? url, CancellationToken ct = default)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return (Unknown, "sem URL");
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "";
        if (host.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase) && ExtractLinkedInJobId(url) is { } jobId)
            return await CheckLinkedInGuestAsync(jobId, ct).ConfigureAwait(false);
        var (status, body) = await FetchUrlTextAsync(url, ct).ConfigureAwait(false);
        if (status == 0) return (Unknown, "falha de rede ou timeout");
        var verdict = ClassifyHttp(status, body);
        return (verdict, $"HTTP {status}" + (verdict == Unknown ? " sem sinal conclusivo" : ""));
    }

    /// <summary>Confere todas as vagas "worth" por HTTP público; só evidência clara de encerramento a move para "ignorada".</summary>
    public async Task<ReverifySummary> ReverifyAsync(CancellationToken ct = default)
    {
        List<(long Id, string? Url)> rows;
        using (var connection = db.Open())
            rows = connection.Query<(long, string?)>("SELECT id, url FROM jobs WHERE status='worth' ORDER BY id ASC").ToList();
        int checkedCount = 0, ignored = 0, active = 0, unknown = 0;
        var aborted = false;
        var changed = new List<long>();
        foreach (var (id, url) in rows)
        {
            if (ct.IsCancellationRequested) { aborted = true; break; }
            string verdict, detail;
            try { (verdict, detail) = await CheckJobAsync(url, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { (verdict, detail) = (Unknown, ex.Message); }
            catch (OperationCanceledException) { aborted = true; break; }
            checkedCount++;
            if (verdict == Inactive)
            {
                using (var connection = db.Open())
                    connection.Execute("UPDATE jobs SET status='ignored', notes=@notes WHERE id=@id AND status='worth'",
                        new { notes = Text.Take($"Ignorada na verificação diária (inativa): {detail}", 500), id });
                ignored++;
                changed.Add(id);
            }
            else if (verdict == Active) active++;
            else unknown++;
        }
        if (changed.Count > 0) db.Bus.Publish(new JobsChanged(changed));
        return new ReverifySummary(checkedCount, ignored, active, unknown, aborted);
    }

    public static string FormatSummary(ReverifySummary summary) =>
        $"Verificação diária: {summary.Checked} checadas, {summary.Ignored} inativas removidas, {summary.Active} ativas, {summary.Unknown} inconclusivas mantidas."
        + (summary.Aborted ? " Interrompida por cancelamento." : "");

    /// <summary>Handler da fila para o tipo <c>worth_reverify</c>.</summary>
    public QueueHandler Handler => async (job, ct) =>
    {
        var message = FormatSummary(await ReverifyAsync(ct).ConfigureAwait(false));
        new EventLogStore(db).Add("info", "worth-check", message);
        return message;
    };
}
