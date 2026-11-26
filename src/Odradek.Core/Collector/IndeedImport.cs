using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Api;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.Core.Collector;

/// <summary>Uma vaga do Indeed lida na aba do usuário, já completa. <see cref="Host"/> é o domínio onde ela foi vista (ex.: <c>br.indeed.com</c>).</summary>
public sealed record IndeedCard(string Id, string Title, string Company, string Location, string Description, string Posted, string Host);

/// <summary>
/// Importa as vagas do Indeed que a extensão leu na aba do usuário. Diferente do LinkedIn, o Indeed não tem página pública que o app consiga
/// buscar (há proteção contra robôs), então a vaga chega completa e o app <b>nunca</b> faz pedidos ao Indeed.
/// </summary>
public static partial class IndeedImport
{
    public const string Source = "Indeed:Recomendadas";
    public const string FilterId = "rec-indeed";
    public const int MaxPerCall = 50;
    public const int MaxIdsPerQuery = 500;
    public const string DefaultHost = "www.indeed.com";

    [GeneratedRegex(@"^[0-9a-f]{16}\z")] private static partial Regex JobKey();
    [GeneratedRegex(@"^([a-z]{2}\.|www\.)?indeed\.com\z")] private static partial Regex IndeedHost();
    [GeneratedRegex(@"[?&]jk=([0-9a-fA-F]{16})(?![0-9a-zA-Z])")] private static partial Regex KeyInUrl();

    /// <summary>16 caracteres hexadecimais: a id vai para a URL gravada, então nada além disso passa.</summary>
    public static bool IsValidId(string? id) => id is not null && JobKey().IsMatch(id);

    /// <summary>O <c>jk</c> de uma URL do Indeed (<c>viewjob</c>, <c>rc/clk</c>, <c>pagead/clk</c>…), de qualquer país; <c>null</c> se não houver.</summary>
    public static string? ExtractJobKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Contains("indeed.", StringComparison.OrdinalIgnoreCase)) return null;
        return KeyInUrl().Match(uri.Query) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static IndeedCard? Card(JsonNode? node)
    {
        if (node is not JsonObject item) return null;
        string Field(string key, int max) => Text.Take(Text.PyStr(item[key]).Trim(), max);
        var id = Text.PyStr(item["id"]).Trim();
        var host = item["host"] is null ? DefaultHost : Text.PyStr(item["host"]).Trim().ToLowerInvariant();
        if (!IsValidId(id) || !IndeedHost().IsMatch(host)) return null;
        return new IndeedCard(id, Field("title", 300), Field("company", 200), Field("location", 200), Field("description", LinkedInImport.MaxDescription), Field("posted", 60), host);
    }

    /// <summary>Jobkeys que o banco já conhece, inclusive das vagas que o Apify trouxe (a URL delas pode ser a do site da empresa: aí vale o <c>source_id</c>).</summary>
    public static HashSet<string> KnownIds(Database db)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        using var connection = db.Open();
        foreach (var (url, sourceId) in connection.Query<(string? Url, string? SourceId)>("SELECT url, source_id FROM jobs WHERE url LIKE '%indeed.%' OR source LIKE '%indeed%'"))
        {
            if (ExtractJobKey(url) is { } fromUrl) known.Add(fromUrl);
            if (IsValidId(sourceId)) known.Add(sourceId!);
            else if (ExtractJobKey(sourceId) is { } fromSourceId) known.Add(fromSourceId);
        }
        return known;
    }

    /// <summary>Data da postagem em ISO; aceita ISO e textos como "há 3 dias" ou "30+ days ago".</summary>
    private static string? PostedIso(string posted)
    {
        if (posted.Length == 0) return null;
        return Clock.TryParse(posted, out var when) ? Clock.ToIso(when) : LinkedInImport.PostedIso(posted.Replace("+", ""));
    }

    /// <param name="evaluateNew">Triagem das vagas novas; só roda com <c>auto_apply</c> ligado.</param>
    /// <param name="report">Chamado na hora em cada desfecho, com a vaga que o gerou.</param>
    public static ImportSummary Run(Database db, IReadOnlyList<IndeedCard> cards, Func<string>? evaluateNew = null, Action<ImportOutcome, IndeedCard>? report = null)
    {
        var known = KnownIds(db);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0, dropped = 0, failed = 0;
        var inserts = new List<long>();
        void Done(IndeedCard card, ImportResult kind, string detail = "", long? jobId = null)
        {
            switch (kind)
            {
                case ImportResult.Inserted: inserts.Add(jobId!.Value); break;
                case ImportResult.Duplicate: duplicates++; break;
                case ImportResult.Dropped: dropped++; break;
                default: failed++; break;
            }
            report?.Invoke(new ImportOutcome(card.Id, kind, detail, jobId, card.Title), card);
        }
        foreach (var card in cards)
        {
            if (!seen.Add(card.Id) || known.Contains(card.Id)) { Done(card, ImportResult.Duplicate); continue; }
            if (card.Title.Length == 0) { Done(card, ImportResult.Failed, "a página veio sem o título da vaga"); continue; }
            var plain = Text.CleanJobHtml(card.Description);
            if (ApifyItems.TextIndicatesClosed(plain)) { Done(card, ImportResult.Dropped, "a vaga foi encerrada"); continue; }
            if (RegionFilter.ExcludedReason(card.Location, card.Title, plain) is { Length: > 0 } excluded) { Done(card, ImportResult.Dropped, excluded); continue; }
            var job = new CollectedJob(Source, card.Id, card.Title, card.Company, card.Location, card.Description, $"https://{card.Host}/viewjob?jk={card.Id}",
                PostedIso(card.Posted), FilterId);
            if (JobStore.Insert(db, job) is { } id) Done(card, ImportResult.Inserted, jobId: id);
            else Done(card, ImportResult.Duplicate); // mesma empresa, título e local já gravados: o fingerprint ignora a query da URL, onde fica o jk
        }
        var triage = LinkedInImport.TriageNew(db, inserts, evaluateNew);
        new EventLogStore(db).Add("success", "collector",
            $"Importação do Indeed: {cards.Count} recebida(s), {inserts.Count} nova(s), {duplicates} já conhecida(s), {dropped} descartada(s), {failed} com falha.");
        return new ImportSummary(cards.Count, duplicates, inserts.Count, dropped, failed, 0, triage);
    }

    private static ApiResult Unknown(Database db, JsonArray asked)
    {
        if (asked.Count == 0) return ApiResult.Error("Nenhuma id na lista.");
        if (asked.Count > MaxIdsPerQuery) return ApiResult.Error($"No máximo {MaxIdsPerQuery} ids por consulta.");
        var ids = asked.Select(node => Text.PyStr(node).Trim()).ToList();
        if (ids.Any(id => !IsValidId(id))) return ApiResult.Error("Cada id precisa ser um jobkey do Indeed.");
        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        var known = KnownIds(db);
        var unknown = distinct.Where(id => !known.Contains(id)).ToList();
        return ApiResult.Ok(new JsonObject
        {
            ["ok"] = true, ["unknown"] = new JsonArray(unknown.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()), ["known"] = distinct.Count - unknown.Count,
        });
    }

    private static ApiResult Progress(IndeedQueue? queue) => ApiResult.Ok(new JsonObject { ["ok"] = true, ["pending"] = queue?.Pending ?? 0, ["cancelled"] = queue?.Cancelled ?? false });

    private static ApiResult Announce(IndeedQueue queue, JsonArray announced)
    {
        if (announced.Count > MaxIdsPerQuery) return ApiResult.Error($"No máximo {MaxIdsPerQuery} vagas por envio.");
        var cards = announced.Select(Card).ToList();
        if (cards.Any(card => card is null)) return ApiResult.Error("Cada vaga precisa de um jobkey e de um endereço do Indeed válidos.");
        queue.Announce(cards.Select(card => new IndeedPending(card!.Id, card.Title, card.Company, card.Location, card.Host)));
        return Progress(queue);
    }

    private static ApiResult Skipped(IndeedQueue queue, JsonArray skipped)
    {
        if (skipped.Count > MaxIdsPerQuery) return ApiResult.Error($"No máximo {MaxIdsPerQuery} vagas por envio.");
        var items = skipped.Select(node => node is JsonObject item ? (Id: Text.PyStr(item["id"]).Trim(), Reason: Text.PyStr(item["reason"])) : (Id: "", Reason: "")).ToList();
        if (items.Any(item => !IsValidId(item.Id) || item.Reason is not ("gone" or "unreadable"))) return ApiResult.Error("Cada item precisa de um jobkey e do motivo (gone ou unreadable).");
        foreach (var (id, reason) in items)
        {
            if (reason == "gone") queue.Skip(id, ImportResult.Dropped, "a vaga não existe mais");
            else queue.Skip(id, ImportResult.Failed, "a aba não conseguiu ler a página da vaga");
        }
        return Progress(queue);
    }

    private static ApiResult Finished(IndeedQueue queue, string reason)
    {
        IndeedState? state = reason switch
        {
            "done" => IndeedState.Done, "blocked" => IndeedState.Blocked, "left" => IndeedState.LeftPage,
            "unreadable" => IndeedState.Unreadable, "error" => IndeedState.Failed, _ => null,
        };
        if (state is not { } known) return ApiResult.Error("Motivo desconhecido.");
        queue.Finish(known);
        return Progress(queue);
    }

    /// <summary>
    /// <c>{"ids":["…"]}</c> consulta quais vagas o app ainda não tem; <c>{"jobs":[{"id","title","company","location","description","posted"?,"host"?}]}</c> grava.
    /// As outras formas só alimentam a tela de Filas: <c>{"queue":[…]}</c> (o que a aba vai ler), <c>{"skipped":[{"id","reason"}]}</c> e <c>{"finished":"done"}</c>.
    /// Quando há fila, as respostas trazem <c>cancelled</c>: o usuário a esvaziou e a extensão deve parar.
    /// </summary>
    public static ApiResult Handle(Database db, JsonObject body, IndeedQueue? queue = null)
    {
        if (body["ids"] is JsonArray asked) return Unknown(db, asked); // consulta, não grava nada
        if (body["queue"] is not null || body["skipped"] is not null || body["finished"] is not null)
        {
            if (queue is null) return ApiResult.Error("Fila indisponível neste momento.", 503);
            if (body["queue"] is JsonArray announced) return Announce(queue, announced);
            if (body["skipped"] is JsonArray skipped) return Skipped(queue, skipped);
            return Finished(queue, Text.PyStr(body["finished"]));
        }
        if (body["jobs"] is not JsonArray jobs) return ApiResult.Error("Envie a lista \"jobs\".");
        if (jobs.Count == 0) return ApiResult.Error("Nenhuma vaga na lista.");
        if (jobs.Count > MaxPerCall) return ApiResult.Error($"No máximo {MaxPerCall} vagas por envio.");
        var cards = jobs.Select(Card).ToList();
        if (cards.Any(card => card is null)) return ApiResult.Error("Cada vaga precisa de um jobkey e de um endereço do Indeed válidos.");
        if (cards.Any(card => card!.Description.Length == 0)) return ApiResult.Error("Cada vaga do Indeed precisa vir com a descrição.");
        var summary = Run(db, cards!, report: queue is null ? null : (outcome, card) => queue.Report(outcome, card.Host)).ToJson();
        summary["cancelled"] = queue?.Cancelled ?? false;
        return ApiResult.Ok(summary);
    }
}
