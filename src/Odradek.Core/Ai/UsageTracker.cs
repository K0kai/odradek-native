using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Data;

namespace Odradek.Core.Ai;

/// <summary>
/// Contadores locais de chamadas de IA por dia e mês UTC (porte de <c>usage_tracker.py</c>), no mesmo JSON <c>ai_usage_json</c>
/// que o app Python lê. É uma estimativa deste app, não a cota oficial do provedor.
/// </summary>
public sealed class UsageTracker(Database db)
{
    public const string StoreKey = "ai_usage_json";
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object _gate = new();

    public void Record(string provider, string model, JsonObject? result, bool ok, bool quotaError, DateTime? now = null)
    {
        try
        {
            var (tokensIn, tokensOut) = ExtractTokens(provider, result);
            lock (_gate)
            {
                using (var connection = db.Open())
                {
                    var raw = connection.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@StoreKey", new { StoreKey }) ?? "";
                    var store = Load(raw);
                    RecordCall(store, provider, model, tokensIn, tokensOut, ok, quotaError, now ?? DateTime.UtcNow);
                    connection.Execute("INSERT INTO settings(key,value) VALUES(@StoreKey,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                        new { StoreKey, value = store.ToJsonString(Compact) });
                }
            }
            db.Bus.Publish(new QuotaChanged());
        }
        catch (Exception)
        {
            // Métrica nunca derruba a chamada de IA.
        }
    }

    public static JsonObject Load(string? raw)
    {
        JsonObject store;
        try { store = !string.IsNullOrWhiteSpace(raw) && JsonNode.Parse(raw) is JsonObject o ? o : new JsonObject(); }
        catch (JsonException) { store = new JsonObject(); }
        if (store["days"] is not JsonObject) store["days"] = new JsonObject();
        if (store["months"] is not JsonObject) store["months"] = new JsonObject();
        return store;
    }

    /// <summary>Tokens (entrada, saída) do formato Gemini ou OpenAI; qualquer outro provedor só conta a chamada.</summary>
    public static (long In, long Out) ExtractTokens(string provider, JsonObject? result)
    {
        if (result is null) return (0, 0);
        switch (provider.ToLowerInvariant().Trim())
        {
            case "gemini":
            {
                var meta = result["usageMetadata"] as JsonObject;
                var tin = Int(meta?["promptTokenCount"]);
                var candidates = meta?["candidatesTokenCount"];
                var tout = Truthy(candidates) ? Int(candidates) : Int(meta?["totalTokenCount"]);
                if (tout != 0 && candidates is null && Truthy(meta?["promptTokenCount"]))
                {
                    var total = Int(meta?["totalTokenCount"]);
                    if (total >= tin) tout = total - tin;
                }
                return (Math.Max(0, tin), Math.Max(0, tout));
            }
            case "openai":
            {
                var usage = result["usage"] as JsonObject;
                var tin = Truthy(usage?["input_tokens"]) ? Int(usage?["input_tokens"]) : Int(usage?["prompt_tokens"]);
                var tout = Truthy(usage?["output_tokens"]) ? Int(usage?["output_tokens"]) : Int(usage?["completion_tokens"]);
                return (Math.Max(0, tin), Math.Max(0, tout));
            }
            default:
                return (0, 0);
        }
    }

    public static void RecordCall(JsonObject store, string provider, string model, long tokensIn, long tokensOut, bool ok, bool quotaError, DateTime when)
    {
        provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.ToLowerInvariant().Trim();
        if (provider.Length == 0) provider = "unknown";
        var utc = when.ToUniversalTime();
        var days = (JsonObject)(store["days"] ??= new JsonObject());
        var months = (JsonObject)(store["months"] ??= new JsonObject());
        var dayKey = $"{provider}|{utc:yyyy-MM-dd}";
        var monthKey = $"{provider}|{utc:yyyy-MM}";
        foreach (var (group, key) in new[] { (days, dayKey), (months, monthKey) })
        {
            var bucket = group[key] as JsonObject ?? EmptyBucket();
            Bump(bucket, tokensIn, tokensOut, ok, quotaError, model);
            group.Remove(key);
            group[key] = bucket;
        }
        Prune(days, 45);
        Prune(months, 6);
    }

    private static JsonObject EmptyBucket() => new()
    {
        ["calls_ok"] = 0, ["calls_error"] = 0, ["calls_quota_error"] = 0, ["tokens_in"] = 0, ["tokens_out"] = 0, ["last_model"] = "",
    };

    private static void Bump(JsonObject bucket, long tokensIn, long tokensOut, bool ok, bool quotaError, string model)
    {
        bucket[ok ? "calls_ok" : "calls_error"] = Int(bucket[ok ? "calls_ok" : "calls_error"]) + 1;
        if (quotaError) bucket["calls_quota_error"] = Int(bucket["calls_quota_error"]) + 1;
        bucket["tokens_in"] = Int(bucket["tokens_in"]) + Math.Max(0, tokensIn);
        bucket["tokens_out"] = Int(bucket["tokens_out"]) + Math.Max(0, tokensOut);
        if (!string.IsNullOrEmpty(model)) bucket["last_model"] = model;
    }

    /// <summary>Mantém só as entradas mais recentes (as chaves começam pelo provedor e terminam na data, então a ordem é a do Python).</summary>
    private static void Prune(JsonObject group, int keep)
    {
        if (group.Count <= keep) return;
        foreach (var key in group.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).Take(group.Count - keep).ToList())
            group.Remove(key);
    }

    private static bool Truthy(JsonNode? node) => Text.Truthy(node);

    private static long Int(JsonNode? node)
    {
        if (node is null) return 0;
        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.Number => (long)node.GetValue<double>(),
                JsonValueKind.String => (long)double.Parse(node.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.True => 1,
                _ => 0,
            };
        }
        catch (Exception) { return 0; }
    }
}
