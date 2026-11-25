using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Api;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.Core.Collector;

/// <summary>
/// Uma vaga lida na página do LinkedIn do usuário. A extensão manda só o cartão (id, título, empresa, local); o Qpid, que abre cada vaga
/// numa sessão já logada, manda também a descrição e a data: com <see cref="Description"/> o app não precisa buscar nada no LinkedIn.
/// </summary>
public sealed record ImportCard(string Id, string Title, string Company, string Location, string Description = "", string Posted = "");

public sealed record ImportSummary(int Received, int Duplicates, int Inserted, int Dropped, int Failed, int Pending, string Triage)
{
    public JsonObject ToJson() => new()
    {
        ["ok"] = true, ["received"] = Received, ["duplicates"] = Duplicates, ["inserted"] = Inserted,
        ["dropped"] = Dropped, ["failed"] = Failed, ["pending"] = Pending, ["triage"] = Triage,
    };
}

/// <summary>O que aconteceu com uma vaga importada. <see cref="JobId"/> só vem nas gravadas; <see cref="Detail"/> explica descartes e falhas.</summary>
public sealed record ImportOutcome(string Id, ImportResult Kind, string Detail, long? JobId, string Title);

public enum ImportResult { Inserted, Duplicate, Dropped, Failed }

/// <summary>
/// Importa as vagas que o LinkedIn recomenda ao usuário. A lista vem da página aberta no navegador dele (extensão); os detalhes vêm do
/// endpoint público <c>jobs-guest</c>, sem login. Nada aqui usa a sessão do LinkedIn.
/// </summary>
public static partial class LinkedInImport
{
    public const string Source = "LinkedIn:Recomendadas";
    public const string FilterId = "rec-linkedin";
    public const int MaxPerCall = 50;
    public const int MaxDescription = 60_000;

    [GeneratedRegex(@"^\d{5,20}$")] private static partial Regex JobId();
    [GeneratedRegex(@"<h[1-6][^>]*top-card-layout__title[^>]*>(.*?)</h[1-6]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex TitleTag();
    [GeneratedRegex(@"<a[^>]*topcard__org-name-link[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex CompanyLink();
    [GeneratedRegex(@"<span[^>]*topcard__flavor(?![-\w])[^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex CompanyFlavor();
    [GeneratedRegex(@"<span[^>]*topcard__flavor--bullet[^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex LocationTag();
    [GeneratedRegex(@"<span[^>]*posted-time-ago__text[^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex PostedTag();
    [GeneratedRegex(@"<div[^>]*show-more-less-html__markup[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex DescriptionTag();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex AnyTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    /// <summary>Só dígitos: a id vai para a URL do endpoint público, então nada além disso passa.</summary>
    public static bool IsValidId(string? id) => id is not null && JobId().IsMatch(id);

    private static string Plain(string html) => Spaces().Replace(WebUtility.HtmlDecode(AnyTag().Replace(html, " ")), " ").Trim();

    private static string First(Regex regex, string html) => regex.Match(html) is { Success: true } m ? Plain(m.Groups[1].Value) : "";

    /// <summary>Lê o HTML público da vaga. Campos que não aparecem voltam vazios (o chamador usa os do cartão).</summary>
    public static (string Title, string Company, string Location, string Posted, string Description) ParseGuest(string html)
    {
        var company = First(CompanyLink(), html);
        if (company.Length == 0) company = First(CompanyFlavor(), html);
        var description = DescriptionTag().Match(html) is { Success: true } d ? d.Groups[1].Value.Trim() : "";
        return (First(TitleTag(), html), company, First(LocationTag(), html), First(PostedTag(), html), description);
    }

    /// <summary>ISO a partir do texto relativo ("2 days ago", "há 3 semanas"); <c>null</c> se não der para inferir.</summary>
    public static string? PostedIso(string posted) =>
        Geo.PostedAgeDays(posted) is { } age ? Clock.ToIso(DateTime.UtcNow.AddDays(-age)) : null;

    public static ImportCard? Card(JsonNode? node)
    {
        if (node is not JsonObject item) return null;
        var id = Text.PyStr(item["id"]).Trim();
        if (!IsValidId(id)) return null;
        string Field(string key, int max) => Text.Take(Text.PyStr(item[key]).Trim(), max);
        return new ImportCard(id, Field("title", 300), Field("company", 200), Field("location", 200), Field("description", MaxDescription), Field("posted", 60));
    }

    /// <summary>Ids de LinkedIn que o banco já conhece, qualquer que seja a forma da URL com que entraram.</summary>
    public static HashSet<string> KnownIds(Database db)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        using var connection = db.Open();
        foreach (var (url, sourceId) in connection.Query<(string? Url, string? SourceId)>("SELECT url, source_id FROM jobs WHERE url LIKE '%linkedin.com%'"))
        {
            if (WorthReverify.ExtractLinkedInJobId(url) is { } fromUrl) known.Add(fromUrl);
            if (IsValidId(sourceId)) known.Add(sourceId!);
        }
        return known;
    }

    public static Task RandomPauseAsync(CancellationToken ct) => Task.Delay(Random.Shared.Next(1500, 3000), ct);

    /// <param name="fetch">GET público: (status, corpo). Em produção, <see cref="WorthReverify.FetchUrlTextAsync"/>.</param>
    /// <param name="pause">Espera entre dois pedidos ao LinkedIn; os testes passam uma que não espera.</param>
    /// <param name="evaluateNew">Triagem das vagas novas; só roda com <c>auto_apply</c> ligado.</param>
    /// <param name="report">Chamado na hora em cada desfecho. As vagas que ficam pendentes (429, cancelamento) não são reportadas.</param>
    public static async Task<ImportSummary> RunAsync(Database db, IReadOnlyList<ImportCard> cards, Func<string, CancellationToken, Task<(int Status, string Body)>> fetch,
        Func<CancellationToken, Task>? pause = null, Func<string>? evaluateNew = null, CancellationToken ct = default, Action<ImportOutcome>? report = null)
    {
        pause ??= RandomPauseAsync;
        evaluateNew ??= () => new QueueActions(db).EvaluateNew();
        var known = KnownIds(db);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0, inserted = 0, dropped = 0, failed = 0, pending = 0;
        var inserts = new List<long>();
        var fetched = 0;
        var blocked = false;
        void Done(ImportCard card, ImportResult kind, string detail = "", long? jobId = null, string? title = null)
        {
            switch (kind)
            {
                case ImportResult.Inserted: inserted++; break;
                case ImportResult.Duplicate: duplicates++; break;
                case ImportResult.Dropped: dropped++; break;
                default: failed++; break;
            }
            report?.Invoke(new ImportOutcome(card.Id, kind, detail, jobId, title ?? card.Title));
        }
        void Save(ImportCard card, string title, string company, string location, string description, string? posted)
        {
            if (title.Length == 0) { Done(card, ImportResult.Failed, "a página veio sem o título da vaga"); return; }
            if (RegionFilter.ExcludedReason(location, title, Text.CleanJobHtml(description)) is { Length: > 0 } excluded) { Done(card, ImportResult.Dropped, excluded, title: title); return; }
            var job = new CollectedJob(Source, card.Id, title, company, location, description, $"https://www.linkedin.com/jobs/view/{card.Id}/", posted, FilterId);
            if (JobStore.Insert(db, job) is { } id) { inserts.Add(id); Done(card, ImportResult.Inserted, jobId: id, title: title); }
            else Done(card, ImportResult.Duplicate, title: title);
        }
        foreach (var card in cards)
        {
            if (!seen.Add(card.Id) || known.Contains(card.Id)) { Done(card, ImportResult.Duplicate); continue; }
            if (card.Description.Length > 0) // já veio completa de uma sessão logada: nada a buscar no LinkedIn
            {
                Save(card, card.Title, card.Company, card.Location, card.Description, card.Posted.Length > 0 ? PostedIso(card.Posted) : null);
                continue;
            }
            if (blocked || ct.IsCancellationRequested) { pending++; continue; }
            if (fetched++ > 0) await pause(ct).ConfigureAwait(false);
            var (status, body) = await fetch($"https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{card.Id}", ct).ConfigureAwait(false);
            if (status == 429) { blocked = true; pending++; continue; } // o LinkedIn pediu para parar: o resto fica para outra rodada
            if (status is 404 or 410) { Done(card, ImportResult.Dropped, $"a vaga não existe mais (HTTP {status})"); continue; }
            if (status != 200 || body.Length == 0)
            {
                Done(card, ImportResult.Failed, status == 200 ? "o LinkedIn devolveu uma página vazia" : status == 0 ? "sem resposta do LinkedIn" : $"o LinkedIn respondeu HTTP {status}");
                continue;
            }
            if (ApifyItems.TextIndicatesClosed(body)) { Done(card, ImportResult.Dropped, "a vaga foi encerrada"); continue; } // HTML cru: o marcador "closed-job" só existe nas classes
            var parsed = ParseGuest(body);
            Save(card, parsed.Title.Length > 0 ? parsed.Title : card.Title, parsed.Company.Length > 0 ? parsed.Company : card.Company,
                parsed.Location.Length > 0 ? parsed.Location : card.Location, parsed.Description, PostedIso(parsed.Posted));
        }
        var triage = "";
        if (inserted > 0)
        {
            db.Bus.Publish(new JobsChanged(inserts));
            if ((new SettingsStore(db).All().GetValueOrDefault("auto_apply") ?? "") == "1") triage = evaluateNew();
        }
        new EventLogStore(db).Add(blocked ? "warning" : "success", "collector",
            $"Importação do LinkedIn: {cards.Count} recebida(s), {inserted} nova(s), {duplicates} já conhecida(s), {dropped} descartada(s), {failed} com falha, {pending} pendente(s)"
            + (blocked ? " (o LinkedIn limitou os pedidos; tente de novo mais tarde)." : "."));
        return new ImportSummary(cards.Count, duplicates, inserted, dropped, failed, pending, triage);
    }

    /// <summary>
    /// <c>{"ids":["4001…"]}</c> → <c>{"ok":true,"unknown":[…],"known":n}</c>: quais ids o app ainda não tem. A extensão só busca a página das desconhecidas,
    /// então vaga já importada não custa nenhum pedido ao LinkedIn.
    /// </summary>
    private static ApiResult Enqueue(LinkedInQueue? queue, JsonArray asked)
    {
        if (queue is null) return ApiResult.Error("Fila indisponível neste momento.", 503);
        if (asked.Count == 0) return ApiResult.Error("Nenhuma id na lista.");
        if (asked.Count > LinkedInQueue.MaxPerCall) return ApiResult.Error($"No máximo {LinkedInQueue.MaxPerCall} ids por envio.");
        var ids = asked.Select(node => Text.PyStr(node).Trim()).ToList();
        if (ids.Any(id => !IsValidId(id))) return ApiResult.Error("Cada id precisa ser numérica, do LinkedIn.");
        return ApiResult.Ok(queue.Enqueue(ids));
    }

    private static ApiResult Unknown(Database db, JsonArray asked)
    {
        if (asked.Count == 0) return ApiResult.Error("Nenhuma id na lista.");
        if (asked.Count > 500) return ApiResult.Error("No máximo 500 ids por consulta.");
        var ids = asked.Select(node => Text.PyStr(node).Trim()).ToList();
        if (ids.Any(id => !IsValidId(id))) return ApiResult.Error("Cada id precisa ser numérica, do LinkedIn.");
        var known = KnownIds(db);
        var unknown = ids.Distinct(StringComparer.Ordinal).Where(id => !known.Contains(id)).ToList();
        return ApiResult.Ok(new JsonObject
        {
            ["ok"] = true, ["unknown"] = new JsonArray(unknown.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()), ["known"] = ids.Distinct(StringComparer.Ordinal).Count() - unknown.Count,
        });
    }

    /// <summary>
    /// Corpo comum das rotas da extensão e do Qpid: <c>{"jobs":[{"id", "title", "company", "location", "description"?, "posted"?}]}</c>.
    /// Só vagas sem descrição precisam do HTTP (endpoint público); se todas vierem completas, <paramref name="http"/> nem é usado.
    /// </summary>
    public static async Task<ApiResult> HandleAsync(Database db, JsonObject body, Func<HttpClient>? http, CancellationToken ct = default, LinkedInQueue? queue = null)
    {
        if (body["ids"] is JsonArray asked) return Unknown(db, asked); // consulta, não grava nada
        if (body["enqueue"] is JsonArray toQueue) return Enqueue(queue, toQueue); // a extensão manda só as ids; o app lê as páginas em segundo plano
        if (body["status"] is not null) return queue is null ? ApiResult.Error("Fila indisponível neste momento.", 503) : ApiResult.Ok(queue.Status());
        if (body["jobs"] is not JsonArray jobs) return ApiResult.Error("Envie a lista \"jobs\".");
        if (jobs.Count == 0) return ApiResult.Error("Nenhuma vaga na lista.");
        if (jobs.Count > MaxPerCall) return ApiResult.Error($"No máximo {MaxPerCall} vagas por envio.");
        var cards = jobs.Select(Card).ToList();
        if (cards.Any(card => card is null)) return ApiResult.Error("Cada vaga precisa de uma id numérica do LinkedIn.");
        Func<string, CancellationToken, Task<(int Status, string Body)>> fetch = (_, _) => Task.FromResult((0, ""));
        if (cards.Any(card => card!.Description.Length == 0))
        {
            var client = http?.Invoke();
            if (client is null) return ApiResult.Error("Importação indisponível neste momento.", 503);
            fetch = new WorthReverify(db, client).FetchUrlTextAsync;
        }
        var summary = await RunAsync(db, cards!, fetch, ct: ct).ConfigureAwait(false);
        return ApiResult.Ok(summary.ToJson());
    }
}
