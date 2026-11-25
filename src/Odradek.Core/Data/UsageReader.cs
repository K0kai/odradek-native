using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;

namespace Odradek.Core.Data;

/// <summary>Um balde de contadores de IA (dia ou mês) do <c>ai_usage_json</c>.</summary>
public sealed record UsageBucket(long CallsOk, long CallsError, long CallsQuotaError, long TokensIn, long TokensOut);

public sealed record AiUsage(string Provider, string Model, string DayLabel, string MonthLabel, UsageBucket Day, UsageBucket Month);

/// <summary>Último snapshot da cota Apify (gravado pelo coletor); a consulta ao vivo vem na etapa 4.</summary>
public sealed record ApifyQuota(double UsedUsd, double ApiLimitUsd, double LocalLimitUsd, string CycleLabel, string FetchedAt, string Error)
{
    public double Limit => LocalLimitUsd > 0 ? LocalLimitUsd : ApiLimitUsd;
    public double Percent => Limit > 0 ? Math.Min(100, Math.Max(0, UsedUsd / Limit * 100)) : 0;
}

public sealed record BackupInfo(bool Ok, string At, long Rows, double Seconds, string Error);

/// <summary>
/// Quanto o banco local ocupa. <see cref="WalBytes"/> são os arquivos <c>-wal</c> e <c>-shm</c>; <see cref="FreeBytes"/> são as páginas vazias
/// dentro do <c>jobs.db</c>, que a compactação devolve ao disco.
/// </summary>
public sealed record StorageInfo(long DbBytes, long WalBytes, long FreeBytes, long Jobs, long JobsWithDescription, long CoverLetters)
{
    public long TotalBytes => DbBytes + WalBytes;
}

public sealed record UsageData(AiUsage Ai, ApifyQuota? Apify, BackupInfo? Backup, StorageInfo? Storage);

/// <summary>Leitura da tela "Uso e cotas" (porte de <c>usage_api_payload</c> e <c>summarize_ai_usage</c>, só o que está no banco).</summary>
public static class UsageReader
{
    public static UsageData Read(Database db, DateTime? now = null)
    {
        var settings = new SettingsStore(db).All();
        string Get(string key) => settings.TryGetValue(key, out var v) ? v : "";

        var provider = PyProvider(Get("ai_provider"));
        var when = (now ?? DateTime.UtcNow).ToUniversalTime();
        var store = Parse(Get("ai_usage_json"));
        var day = Bucket(store, "days", $"{provider}|{when:yyyy-MM-dd}");
        var month = Bucket(store, "months", $"{provider}|{when:yyyy-MM}");
        var ai = new AiUsage(provider, Get("ai_model"), when.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            when.ToString("MM/yyyy", CultureInfo.InvariantCulture), day, month);
        return new UsageData(ai, Apify(Parse(Get("apify_quota_snapshot_json"))), Backup(Parse(Get("standby_last_sync_json"))), Storage(db));
    }

    /// <summary>Tamanho em disco do banco e dos arquivos auxiliares do SQLite (0 para o que não existe). Só olha o sistema de arquivos.</summary>
    public static (long Db, long Wal) FileSizes(string path)
    {
        static long Size(string file) { var info = new FileInfo(file); return info.Exists ? info.Length : 0; }
        return (Size(path), Size(path + "-wal") + Size(path + "-shm"));
    }

    public static StorageInfo? Storage(Database db)
    {
        if (!File.Exists(db.Path)) return null;
        using var connection = db.Open();
        long Count(string sql) => connection.ExecuteScalar<long>(sql);
        var free = Count("PRAGMA freelist_count") * Count("PRAGMA page_size");
        var jobs = Count("SELECT COUNT(*) FROM jobs");
        var described = Count("SELECT COUNT(*) FROM jobs WHERE description <> ''");
        var letters = Count("SELECT COUNT(*) FROM cover_letters");
        var (main, wal) = FileSizes(db.Path);
        return new StorageInfo(main, wal, free, jobs, described, letters);
    }

    // (value or "gemini").casefold(): vazio cai em gemini.
    private static string PyProvider(string value) => string.IsNullOrEmpty(value) ? "gemini" : value.ToLowerInvariant();

    private static JsonObject Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try { return JsonNode.Parse(raw) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static long Int(JsonNode? node)
    {
        try { return node is null ? 0 : (long)Math.Round(node.GetValue<double>()); } catch { return 0; }
    }

    private static double Num(JsonNode? node)
    {
        try { return node is null ? 0 : node.GetValue<double>(); } catch { return 0; }
    }

    private static string Str(JsonNode? node)
    {
        try { return node?.GetValue<string>() ?? ""; } catch { return ""; }
    }

    private static UsageBucket Bucket(JsonObject store, string group, string key)
    {
        var node = (store[group] as JsonObject)?[key] as JsonObject;
        return new UsageBucket(Int(node?["calls_ok"]), Int(node?["calls_error"]), Int(node?["calls_quota_error"]),
            Int(node?["tokens_in"]), Int(node?["tokens_out"]));
    }

    private static ApifyQuota? Apify(JsonObject snapshot)
    {
        if (snapshot.Count == 0) return null;
        return new ApifyQuota(Num(snapshot["used_usd"]), Num(snapshot["api_limit_usd"]), Num(snapshot["local_limit_usd"]),
            Str(snapshot["cycle_label"]), Str(snapshot["fetched_at"]), Str(snapshot["error"]));
    }

    private static BackupInfo? Backup(JsonObject last) =>
        last.Count == 0 ? null : new BackupInfo(last["ok"]?.GetValue<bool>() ?? false, Str(last["at"]), Int(last["rows"]), Num(last["seconds"]), Str(last["error"]));
}
