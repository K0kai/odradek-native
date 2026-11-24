using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odradek.Core.Actions;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.Core.Engines;

/// <summary>
/// Cotas externas exibidas na aba Uso: créditos do Apify (a cada 15 s com coleta ativa, 5 min parado) e plano gratuito do TokenHarbor
/// (cache de 60 s). Nunca derrubam quem as consulta: falha vira um snapshot com o campo <c>error</c> (<c>app.py:841-960</c>).
/// </summary>
public sealed class QuotaService(Database db, ApifyClient client, Func<string, string>? secret = null)
{
    public const string ApifySnapshotKey = "apify_quota_snapshot_json";
    public const string TokenHarborQuotaUrl = "https://tokenharbor.ai/api/cli/free-tier"; // na raiz do site, não em /v1; não consome a cota
    public const double LiveRefreshSeconds = 15;
    public const double IdleRefreshSeconds = 300;
    public const double TokenHarborCacheSeconds = 60;

    private readonly SettingsStore _settings = new(db);
    private readonly Func<string, string> _secret = secret ?? (name => SecretStore.Get(name));
    private readonly SemaphoreSlim _apifyLock = new(1, 1);
    private readonly SemaphoreSlim _tokenHarborLock = new(1, 1);
    private (double ExpiresAt, JsonObject Snapshot)? _tokenHarborCache;

    private static double MonotonicSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary><c>float(x)</c> do Python para o valor JSON; 0 quando vazio.</summary>
    private static double Float(JsonNode? node)
    {
        if (!Text.Truthy(node)) return 0.0;
        if (node is JsonValue v && v.TryGetValue<bool>(out var flag)) return flag ? 1.0 : 0.0;
        return double.TryParse(Text.PyStrRaw(node).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new FormatException("float inválido");
    }

    /// <summary>Número com o texto que o Python escreveria (<c>5.0</c>, não <c>5</c>).</summary>
    private static JsonNode PyFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);
        if (!text.Contains('.') && !text.Contains('E') && !text.Contains('e')) text += ".0";
        return JsonNode.Parse(text)!;
    }

    private static int Percent(JsonNode? node)
    {
        try { return (int)Math.Max(0, Math.Min(100, Math.Truncate(Float(node)))); }
        catch (FormatException) { return 0; }
    }

    public static JsonObject ParseApifyLimits(JsonNode? payload, double localLimitUsd, string? nowIso = null)
    {
        var root = payload as JsonObject ?? new JsonObject();
        var data = root.ContainsKey("data") ? root["data"] as JsonObject ?? new JsonObject() : root;
        var limits = data["limits"] as JsonObject ?? new JsonObject();
        var current = data["current"] as JsonObject ?? new JsonObject();
        var cycle = (Text.Truthy(data["monthlyUsageCycle"]) ? data["monthlyUsageCycle"] : data["usageCycle"]) as JsonObject ?? new JsonObject();
        var used = Float(current["monthlyUsageUsd"]);
        var apiLimit = Float(limits["maxMonthlyUsageUsd"]);
        var local = Math.Max(0.0, localLimitUsd);
        var pctLocal = local > 0 ? used / local * 100.0 : 0.0;
        var pctApi = apiLimit > 0 ? used / apiLimit * 100.0 : 0.0;
        return new JsonObject
        {
            ["used_usd"] = PyFloat(used), ["api_limit_usd"] = PyFloat(apiLimit), ["local_limit_usd"] = PyFloat(local),
            ["cycle_label"] = BrDates.FormatCycleRange(Text.PyStr(cycle["startAt"]), Text.PyStr(cycle["endAt"])),
            ["pct_of_local"] = PyFloat(Math.Min(999.0, pctLocal)), ["pct_of_api"] = PyFloat(Math.Min(999.0, pctApi)),
            ["fetched_at"] = nowIso ?? Clock.NowIso(), ["error"] = "",
        };
    }

    public static JsonObject ParseTokenHarborFreeTier(JsonNode? payload, string? nowIso = null)
    {
        var data = payload as JsonObject ?? new JsonObject();
        return new JsonObject
        {
            ["configured"] = true, ["plan_name"] = Text.PyStr(data["plan_name"]), ["on_plan"] = Text.Truthy(data["on_plan"]),
            ["plan_used_pct"] = Percent(data["plan_used_pct"]), ["plan_exhausted"] = Text.Truthy(data["plan_exhausted"]),
            ["used_pct"] = Percent(data["used_pct"]), ["exhausted"] = Text.Truthy(data["exhausted"]), ["activated"] = Text.Truthy(data["activated"]),
            ["period_started_at"] = Text.PyStr(data["period_started_at"]),
            ["reset_at"] = Text.Truthy(data["reset_at"]) ? Text.PyStrRaw(data["reset_at"]) : Text.PyStr(data["period_ends_at"]),
            ["models"] = new JsonArray((data["models"] as JsonArray ?? []).Select(m => (JsonNode?)Text.PyStrRaw(m)).ToArray()),
            ["models_enabled"] = !data.ContainsKey("models_enabled") || Text.Truthy(data["models_enabled"]),
            ["fetched_at"] = nowIso ?? Clock.NowIso(), ["error"] = "",
        };
    }

    // ------------------------------------------------------------------ Apify

    public JsonObject LoadApifySnapshot()
    {
        var raw = _settings.Get(ApifySnapshotKey).Trim();
        if (raw.Length == 0) return new JsonObject();
        try { return JsonNode.Parse(raw) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private double LocalLimit() =>
        double.TryParse(_settings.Get("apify_monthly_credit_limit_usd", "5").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) ? limit : 5.0;

    private static double? SnapshotAgeSeconds(JsonObject snapshot) =>
        Clock.TryParse(Text.PyStr(snapshot["fetched_at"]), out var fetched) ? (DateTime.UtcNow - fetched).TotalSeconds : null;

    private JsonObject Empty(double localLimit, string error) => new()
    {
        ["used_usd"] = PyFloat(0), ["api_limit_usd"] = PyFloat(0), ["local_limit_usd"] = PyFloat(localLimit), ["cycle_label"] = "—",
        ["pct_of_local"] = PyFloat(0), ["pct_of_api"] = PyFloat(0), ["fetched_at"] = "", ["error"] = error,
    };

    private async Task<JsonObject> FetchApifySnapshotAsync(CancellationToken ct)
    {
        var token = _secret("apify_token");
        var localLimit = LocalLimit();
        if (token.Length == 0)
        {
            var empty = Empty(localLimit, "Token Apify não configurado (IA & integrações).");
            _settings.Set(ApifySnapshotKey, PyJson.Serialize(empty));
            db.Bus.Publish(new QuotaChanged());
            return empty;
        }
        JsonObject snapshot;
        try
        {
            var payload = await client.FetchJsonAsync($"{ApifyActors.Api}/users/me/limits", bearer: token, timeoutSeconds: 30, ct: ct).ConfigureAwait(false);
            snapshot = ParseApifyLimits(payload, localLimit);
            // Alinha o "último uso" que o coletor lê; tudo numa gravação só.
            _settings.SetMany(new Dictionary<string, string>
            {
                ["apify_last_usage_usd"] = ApifyClient.FormatMoney(Float(snapshot["used_usd"]), 4),
                ["apify_last_usage_cycle"] = Text.PyStr(snapshot["cycle_label"]),
                [ApifySnapshotKey] = PyJson.Serialize(snapshot),
            });
            db.Bus.Publish(new QuotaChanged());
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            snapshot = Empty(localLimit, $"Falha ao consultar Apify: {ex.Message}");
        }
        _settings.Set(ApifySnapshotKey, PyJson.Serialize(snapshot));
        db.Bus.Publish(new QuotaChanged());
        return snapshot;
    }

    /// <summary>Busca os limites oficiais e grava o cache. Com <paramref name="minAgeSeconds"/>, devolve o cache se for mais novo que isso.</summary>
    public async Task<JsonObject> RefreshApifyAsync(double minAgeSeconds = 0, CancellationToken ct = default)
    {
        await _apifyLock.WaitAsync(ct).ConfigureAwait(false); // quem espera já encontra o snapshot fresco
        try
        {
            if (minAgeSeconds > 0)
            {
                var cached = LoadApifySnapshot();
                if (cached.Count > 0 && !Text.Truthy(cached["error"]) && SnapshotAgeSeconds(cached) is { } age && age < minAgeSeconds) return cached;
            }
            return await FetchApifySnapshotAsync(ct).ConfigureAwait(false);
        }
        finally { _apifyLock.Release(); }
    }

    /// <summary>Para uso dentro do coletor: falha de cota nunca derruba a coleta.</summary>
    public async Task RefreshApifyQuietlyAsync(double minAgeSeconds = 0)
    {
        try { await RefreshApifyAsync(minAgeSeconds).ConfigureAwait(false); } catch (Exception) { }
    }

    /// <summary>Snapshot para o painel, atualizado sozinho: a cada 15 s com coleta ativa, a cada 5 min parado.</summary>
    public async Task<JsonObject> FreshApifyAsync(bool collectorRunning, CancellationToken ct = default)
    {
        bool hasToken;
        try { hasToken = _secret("apify_token").Length > 0; } catch (Exception) { hasToken = false; }
        if (!hasToken) return LoadApifySnapshot();
        try { return await RefreshApifyAsync(collectorRunning ? LiveRefreshSeconds : IdleRefreshSeconds, ct).ConfigureAwait(false); }
        catch (Exception) { return LoadApifySnapshot(); }
    }

    // ------------------------------------------------------------------ TokenHarbor

    public async Task<JsonObject> TokenHarborSnapshotAsync(bool force = false, CancellationToken ct = default)
    {
        await _tokenHarborLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _tokenHarborCache is { } cache && cache.ExpiresAt > MonotonicSeconds) return cache.Snapshot;
            string key;
            JsonObject snapshot;
            try { key = _secret("tokenharbor"); snapshot = new JsonObject { ["configured"] = false }; }
            catch (Exception ex)
            {
                key = "";
                snapshot = new JsonObject { ["configured"] = true, ["error"] = $"Não foi possível ler a chave do TokenHarbor: {ex.Message}", ["fetched_at"] = "" };
            }
            if (key.Length > 0)
            {
                try
                {
                    var payload = await client.FetchJsonAsync(TokenHarborQuotaUrl, bearer: key, timeoutSeconds: 15, ct: ct).ConfigureAwait(false);
                    snapshot = ParseTokenHarborFreeTier(payload);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    snapshot = new JsonObject
                    {
                        ["configured"] = true, ["error"] = $"Falha ao consultar o TokenHarbor: {ex.Message.Replace(key, "[chave omitida]", StringComparison.Ordinal)}", ["fetched_at"] = "",
                    };
                }
            }
            _tokenHarborCache = (MonotonicSeconds + TokenHarborCacheSeconds, snapshot);
            return snapshot;
        }
        finally { _tokenHarborLock.Release(); }
    }
}
