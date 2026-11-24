using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Odradek.Core.Collector;

/// <summary>Erro com o nome da exceção do Python (as mensagens do ciclo mostram <c>fonte: RuntimeError</c>).</summary>
public sealed class CollectorException(string pythonName, string message) : Exception(message)
{
    public string PythonName { get; } = pythonName;
    public static CollectorException Runtime(string message) => new("RuntimeError", message);
    public static CollectorException Value(string message) => new("ValueError", message);
}

/// <summary>Datas legíveis do painel (<c>dates_br.py</c>): dd/mm/aaaa em Brasília.</summary>
public static partial class BrDates
{
    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})")] private static partial Regex IsoPrefix();
    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})/(\d{4})$")] private static partial Regex BrDate();

    public static TimeZoneInfo Brasilia { get; } = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
    }

    public static DateTime BrasiliaNow() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Brasilia);

    /// <summary>Data dd/mm/aaaa; ISO com hora usa só a parte do calendário (sem deslocar o dia por fuso).</summary>
    public static string FormatDate(string? value)
    {
        var raw = (value ?? "").Trim();
        if (raw.Length == 0) return "";
        var br = BrDate().Match(raw);
        if (br.Success) return $"{int.Parse(br.Groups[1].Value):00}/{int.Parse(br.Groups[2].Value):00}/{br.Groups[3].Value}";
        var iso = IsoPrefix().Match(raw);
        if (iso.Success) return $"{int.Parse(iso.Groups[3].Value):00}/{int.Parse(iso.Groups[2].Value):00}/{iso.Groups[1].Value}";
        if (!Clock.TryParse(raw, out var utc)) return raw.Length >= 10 ? raw[..10] : raw;
        return TimeZoneInfo.ConvertTimeFromUtc(utc, Brasilia).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    public static string FormatCycleRange(string? start, string? end)
    {
        var (s, e) = (FormatDate(start), FormatDate(end));
        if (s.Length > 0 && e.Length > 0) return $"{s} a {e}";
        return s.Length > 0 ? s : e.Length > 0 ? e : "ciclo atual";
    }
}

/// <summary>Chamadas HTTP JSON das fontes e da API da Apify (<c>fetch_json</c> do <c>app.py</c>: mesmo User-Agent, erros com o texto do Python).</summary>
public sealed class ApifyClient(HttpClient http)
{
    public const string UserAgent = "JobScraperLocal/0.1 (personal job search)";

    public static string FormatMoney(double value, int digits) => Text.PyRound(value, digits).ToString("F" + digits, CultureInfo.InvariantCulture);

    public async Task<JsonNode?> FetchJsonAsync(string url, string? body = null, string? bearer = null, int timeoutSeconds = 25, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        string raw;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            raw = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = Text.Take(raw, 400);
                throw CollectorException.Runtime($"HTTP {(int)response.StatusCode}: {(detail.Length > 0 ? detail : response.ReasonPhrase)}");
            }
        }
        catch (HttpRequestException ex) { throw CollectorException.Runtime($"Falha de rede ({ex.Message})"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw CollectorException.Runtime("Falha de rede (tempo esgotado)"); }
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try { return JsonNode.Parse(raw); }
        catch (JsonException ex) { throw CollectorException.Runtime($"Resposta inválida ({ex.Message})"); }
    }

    private static JsonObject Data(JsonNode? payload) => payload is JsonObject o ? (o["data"] as JsonObject ?? o) : new JsonObject();

    private static double Money(JsonNode? node) => node is null ? 0.0 : double.TryParse(Text.PyStrRaw(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;

    /// <summary>Uso do ciclo mensal atual (créditos em USD) e o rótulo do ciclo.</summary>
    public async Task<(double Used, string Cycle)> MonthlyUsageAsync(string token, CancellationToken ct = default)
    {
        var data = Data(await FetchJsonAsync($"{ApifyActors.Api}/users/me/usage/monthly", bearer: token, timeoutSeconds: 30, ct: ct).ConfigureAwait(false));
        var used = Text.Truthy(data["totalUsageCreditsUsdAfterVolumeDiscount"]) ? Money(data["totalUsageCreditsUsdAfterVolumeDiscount"]) : Money(data["totalUsageCreditsUsdBeforeVolumeDiscount"]);
        var cycle = data["usageCycle"] as JsonObject ?? new JsonObject();
        return (used, BrDates.FormatCycleRange(Text.PyStr(cycle["startAt"]), Text.PyStr(cycle["endAt"])));
    }

    public sealed record Options(TimeSpan PollInterval, TimeSpan Deadline)
    {
        public static Options Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(180));
    }

    /// <summary>Executa um actor, espera terminar (até 3 min) e devolve as vagas normalizadas.</summary>
    public async Task<List<CollectedJob>> RunActorAsync(string token, JsonObject actor, IReadOnlyDictionary<string, string> cfg, JsonObject? filter, List<string>? urls,
        Func<JsonObject?>? mainFilter, Action<string, string> log, Func<double, Task>? refreshQuota, CancellationToken stop, Options? options = null)
    {
        options ??= Options.Default;
        var actorId = ApifyActors.NormalizeId(Text.PyStrRaw(actor["id"]));
        var label = Text.Truthy(actor["label"]) ? Text.PyStrRaw(actor["label"]) : actorId;
        var runInput = ApifyActors.BuildRunInput(actor, cfg, filter, urls, mainFilter);
        log("info", $"Iniciando actor {label} ({actorId}); quantidade solicitada: {(runInput["count"] is { } c ? Text.PyStrRaw(c) : "definida pelo template")}.");
        // O limite da busca vale de verdade: maxItems faz o actor (pago por resultado) parar de cobrar no teto e, no fim, o excedente é cortado aqui.
        var limit = RunLimit(actor);
        var started = await FetchJsonAsync($"{ApifyActors.Api}/actors/{LinkedInSearch.QuotePlus(actorId)}/runs?maxItems={limit}", runInput.ToJsonString(), token, 45).ConfigureAwait(false);
        var run = Data(started);
        var runId = Text.PyStr(run["id"]);
        var datasetId = Text.PyStr(run["defaultDatasetId"]);
        if (runId.Length == 0) throw CollectorException.Runtime($"Apify ({label}) não retornou o identificador da execução.");
        var deadline = DateTime.UtcNow + options.Deadline;
        var status = Text.Truthy(run["status"]) ? Text.PyStrRaw(run["status"]) : "READY";
        while (status is "READY" or "RUNNING" && DateTime.UtcNow < deadline)
        {
            if (stop.IsCancellationRequested) throw CollectorException.Runtime($"Coleta interrompida durante Apify ({label}).");
            try { await Task.Delay(options.PollInterval, stop).ConfigureAwait(false); } catch (OperationCanceledException) { /* a parada é tratada no topo do laço */ }
            if (stop.IsCancellationRequested) continue;
            run = Data(await FetchJsonAsync($"{ApifyActors.Api}/actor-runs/{LinkedInSearch.QuotePlus(runId)}", bearer: token, timeoutSeconds: 30).ConfigureAwait(false));
            status = Text.Truthy(run["status"]) ? Text.PyStrRaw(run["status"]) : status;
            datasetId = Text.Truthy(run["defaultDatasetId"]) ? Text.PyStrRaw(run["defaultDatasetId"]) : datasetId;
            if (refreshQuota is not null) await refreshQuota(15).ConfigureAwait(false); // a cota aparece ao vivo enquanto o actor roda
        }
        if (status != "SUCCEEDED") throw CollectorException.Runtime($"Apify ({label}) encerrada com status {status}.");
        if (datasetId.Length == 0) throw CollectorException.Runtime($"Apify ({label}) não produziu dataset.");
        var items = await FetchJsonAsync($"{ApifyActors.Api}/datasets/{LinkedInSearch.QuotePlus(datasetId)}/items?clean=1", bearer: token, timeoutSeconds: 45).ConfigureAwait(false);
        var normalized = ApifyItems.Normalize((items as JsonArray)?.ToList() ?? [], label, LinkedInSearch.MaxAgeDays(cfg), LinkedInSearch.PreferredCountries(cfg), log);
        if (normalized.Count > limit)
        {
            log("info", $"Actor {label}: devolveu {normalized.Count} vaga(s); mantidas as {limit} primeiras (limite da busca).");
            normalized = normalized.Take(limit).ToList();
        }
        log("success", $"Actor {label}: {normalized.Count} vaga(s) úteis.");
        return normalized;
    }

    /// <summary>Quantidade pedida para a execução (<c>count</c> do actor, de 1 a 100; 25 se faltar).</summary>
    public static int RunLimit(JsonObject actor) =>
        Text.Truthy(actor["count"]) && ApifyActors.PyInt(actor["count"]) is { } n ? (int)Math.Max(1, Math.Min(100, n)) : 25;
}
