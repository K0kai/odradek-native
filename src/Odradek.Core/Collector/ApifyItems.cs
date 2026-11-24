using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Odradek.Core.Collector;

/// <summary>Vaga no formato comum das fontes (as chaves <c>_linkedin_filter_id</c> e <c>_search_remote</c> do Python viram campos).</summary>
public sealed record CollectedJob(string Source, string SourceId, string Title, string Company, string Location, string Description, string Url,
    string? PostedAt, string FilterId = "", string SearchRemote = "");

/// <summary>Detecção de vaga encerrada nos itens públicos do Apify (<c>jobs/worth_reverify.py</c>) e normalização para o formato comum (<c>app.py:1791-1977</c>).</summary>
public static partial class ApifyItems
{
    public const int LinkedInMaxAgeDays = 7;

    [GeneratedRegex(
        @"no\s+longer\s+accept\w*\s+(?:applications?|applicants?|candidates?)|" +
        @"no\s+longer\s+open|this\s+job\s+is\s+no\s+longer\s+available|" +
        @"job\s+has\s+been\s+filled|position\s+has\s+been\s+filled|" +
        @"(?:is\s+)?not\s+(?:currently\s+)?accepting\s+(?:applications?|applicants?|candidates?)|" +
        @"n[aã]o\s+aceita(?:mos)?\s+(?:mais\s+)?candidaturas|closed-job(?:__flavor)?|" +
        @"esta\s+vaga\s+n[aã]o\s+est[aá]\s+mais\s+aceitando|" +
        @"esta\s+vaga\s+n[aã]o\s+est[aá]\s+mais\s+dispon[ií]vel|" +
        @"vaga\s+(?:encerrada|expirada|indispon[ií]vel)|" +
        @"n[aã]o\s+est(?:[aá]|amos)\s+mais\s+aceitando\s+candidaturas|" +
        @"n[aã]o\s+estamos\s+mais\s+recebendo\s+candidaturas|" +
        @"\bcandidaturas?\s+encerradas?\b|inscri[cç][oõ]es?\s+encerradas?|" +
        @"application\s+deadline\s+has\s+passed|this\s+position\s+is\s+closed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Inactive();

    public static bool TextIndicatesClosed(string? text) => Inactive().IsMatch(text ?? "");

    private static readonly string[] ClosedFlags = ["closed", "isClosed", "jobClosed", "applicationsClosed", "isJobClosed", "expired", "isExpired"];
    private static readonly string[] ClosedBlobKeys =
    [
        "title", "position", "jobTitle", "displayTitle", "descriptionText", "descriptionHtml", "description", "jobDescription",
        "jobDescriptionHTML", "closedJobText", "jobStateMessage",
    ];

    private static string S(JsonNode? node) => node is null ? "" : Text.PyStr(node);

    /// <summary>Primeiro valor "verdadeiro" (no sentido do Python) entre as chaves.</summary>
    private static JsonNode? First(JsonObject item, params string[] keys)
    {
        foreach (var key in keys)
            if (Text.Truthy(item[key])) return item[key];
        return null;
    }

    public static bool IndicatesClosed(JsonObject item)
    {
        foreach (var key in ClosedFlags)
        {
            var value = item[key];
            if (value is JsonValue v && v.TryGetValue<bool>(out var flag) && flag) return true;
            if (value is not null && S(value).Trim().ToLowerInvariant() is "1" or "true" or "yes") return true;
        }
        var state = S(First(item, "jobState", "jobStatus", "listingStatus")).Trim().ToLowerInvariant();
        if (state is "closed" or "expired" or "inactive" or "filled" or "archived") return true;
        return TextIndicatesClosed(string.Join(' ', ClosedBlobKeys.Select(key => Text.Truthy(item[key]) ? S(item[key]) : "")));
    }

    /// <summary>URL aplicável de payloads LinkedIn/Indeed (inclui <c>viewJobLink</c> relativo).</summary>
    public static string ItemUrl(JsonObject item)
    {
        foreach (var key in new[] { "originalApplyUrl", "link", "url", "applyUrl", "jobUrl", "externalApplyLink", "viewJobLink", "jobLink" })
        {
            var url = Text.Truthy(item[key]) ? S(item[key]).Trim() : "";
            if (url.Length == 0) continue;
            if (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal)) return url;
            if (!url.StartsWith('/')) continue;
            var baseUrl = "";
            foreach (var hintKey in new[] { "companyOverviewLink", "companyUrl", "url", "link" })
            {
                var hint = Text.Truthy(item[hintKey]) ? S(item[hintKey]).Trim() : "";
                if ((hint.StartsWith("http://", StringComparison.Ordinal) || hint.StartsWith("https://", StringComparison.Ordinal))
                    && Uri.TryCreate(hint, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0)
                {
                    baseUrl = $"{parsed.Scheme}://{parsed.Authority}";
                    break;
                }
            }
            if (baseUrl.Length == 0) baseUrl = "https://www.indeed.com"; // Indeed é o padrão quando só vem o caminho relativo
            return baseUrl.TrimEnd('/') + url;
        }
        return "";
    }

    private static bool IsNumber(JsonNode? node) => node is JsonValue v && !v.TryGetValue<string>(out _) && !v.TryGetValue<bool>(out _)
        && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static double NumberOf(JsonNode node) => double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Idade em dias da postagem; aceita número (segundos ou milissegundos), ISO e textos relativos.</summary>
    public static double? PostedAgeDays(JsonNode? postedAt)
    {
        if (postedAt is null) return null;
        if (IsNumber(postedAt))
        {
            var ts = NumberOf(postedAt);
            if (ts > 10_000_000_000) ts /= 1000.0;
            try { return Math.Max(0.0, (DateTime.UtcNow - DateTime.UnixEpoch.AddSeconds(ts)).TotalSeconds / 86400.0); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        var raw = S(postedAt);
        return raw.Length == 0 ? null : Geo.PostedAgeDays(raw);
    }

    /// <summary>True se a vaga cabe na janela (ou se a data é desconhecida: não descartamos).</summary>
    public static bool WithinMaxAge(JsonNode? postedAt, int maxDays = LinkedInMaxAgeDays) => PostedAgeDays(postedAt) is not { } age || age <= maxDays;

    /// <summary>Converte os itens do actor; descarta vagas fechadas e, no LinkedIn, as mais antigas que a janela. LinkedIn sai ordenado (países com viés, mais recentes).</summary>
    public static List<CollectedJob> Normalize(IEnumerable<JsonNode?> items, string label, int maxAgeDays = LinkedInMaxAgeDays,
        IReadOnlyList<string>? preferredCountries = null, Action<string, string>? log = null)
    {
        var results = new List<(CollectedJob Job, double Age)>();
        var sourceName = label.Length > 0 ? $"Apify:{label}" : "Apify";
        var isLinkedIn = label.Contains("linkedin", StringComparison.OrdinalIgnoreCase);
        var skippedClosed = 0;
        foreach (var node in items)
        {
            if (node is not JsonObject item) continue;
            if (IndicatesClosed(item)) { skippedClosed++; continue; }
            var url = ItemUrl(item);
            var title = S(First(item, "title", "position", "jobTitle", "displayTitle")).Trim();
            if (url.Length == 0 || title.Length == 0) continue;
            var description = S(First(item, "descriptionText", "descriptionHtml", "description", "jobDescription", "jobDescriptionHTML"));
            var companyNode = First(item, "companyName", "company", "companyDetails", "jobSourceName");
            var company = companyNode is JsonObject co ? S(First(co, "name", "display_name")) : S(companyNode);
            var locationNode = First(item, "formattedLocation", "location", "jobLocation");
            string location;
            if (locationNode is JsonObject lo)
            {
                location = lo["formatted"] is JsonObject formatted && Text.Truthy(lo["formatted"])
                    ? S(First(formatted, "long", "short") ?? First(lo, "fullAddress", "display_name", "name"))
                    : S(First(lo, "fullAddress", "display_name", "name"));
            }
            else location = S(locationNode);
            if (location.Length == 0)
            {
                var city = S(item["jobLocationCity"] is { } c && Text.Truthy(c) ? c : null).Trim();
                var state = S(item["jobLocationState"] is { } st && Text.Truthy(st) ? st : null).Trim();
                location = string.Join(", ", new[] { city, state }.Where(p => p.Length > 0));
            }
            var postedNode = First(item, "postedAt", "publishedAt", "date", "pubDate");
            string? postedText;
            var ageNode = postedNode;
            if (IsNumber(postedNode) && NumberOf(postedNode!) > 10_000_000_000)
            {
                postedText = Clock.ToIso(DateTime.UnixEpoch.AddSeconds(Math.Floor(NumberOf(postedNode!) / 1000.0))); // Indeed manda pubDate em milissegundos
                ageNode = JsonValue.Create(postedText);
            }
            else postedText = postedNode is null or JsonObject or JsonArray ? null : S(postedNode);
            var age = postedText is null ? null : PostedAgeDays(ageNode);
            if (isLinkedIn && age is { } a && a > maxAgeDays) continue; // teto duro de dias
            var sourceId = Text.Truthy(item["id"]) ? S(item["id"]) : Text.Truthy(item["jobId"]) ? S(item["jobId"]) : url;
            results.Add((new CollectedJob(sourceName, sourceId, title, company, location, description, url, postedText), age ?? 9999.0));
        }
        if (skippedClosed > 0)
            log?.Invoke("info", $"{(label.Length > 0 ? label : "Apify")}: {skippedClosed} vaga(s) descartada(s) no filtro (fechadas / sem candidaturas).");
        if (isLinkedIn && results.Count > 0)
        {
            var preferred = preferredCountries ?? [];
            // Prioriza países com viés e, dentro deles, o mais recente (o mesmo dia primeiro).
            return results.OrderBy(r => Geo.RecencyKey(r.Job.Location, null, preferred).Group).ThenBy(r => r.Age).ThenBy(r => r.Job.Title, StringComparer.Ordinal)
                .Select(r => r.Job).ToList();
        }
        return results.Select(r => r.Job).ToList();
    }

    /// <summary>Vagas da Índia saem antes de entrar na coleta (mesma regra da triagem).</summary>
    public static (List<CollectedJob> Kept, int Dropped) DropExcludedRegion(List<CollectedJob> batch)
    {
        var kept = batch.Where(job => RegionFilter.ExcludedReason(job.Location, job.Title, job.Description).Length == 0).ToList();
        return (kept, batch.Count - kept.Count);
    }
}
