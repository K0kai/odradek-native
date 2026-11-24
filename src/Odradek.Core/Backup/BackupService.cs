using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;
using Odradek.Core.Actions;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.Core.Backup;

/// <summary>Resultado de um backup, no mesmo formato que o app Python grava em <c>standby_last_sync_json</c> (a aba Uso lê isso).</summary>
public sealed record BackupResult(bool Ok, string At, string Error, IReadOnlyDictionary<string, int> Tables, long Rows, double Seconds)
{
    public JsonObject ToJson()
    {
        var tables = new JsonObject();
        foreach (var (name, count) in Tables) tables[name] = count;
        return new JsonObject
        {
            ["ok"] = Ok, ["at"] = At, ["error"] = Error, ["tables"] = tables, ["rows"] = Rows,
            ["seconds"] = Math.Round(Seconds, 1),
        };
    }
}

/// <summary>
/// Backup do banco local no Postgres de backup (<c>run_standby_sync</c>, <c>_daily_standby_sync_loop</c> e <c>backup_on_exit</c> do app Python):
/// uma cópia por dia a partir das 04:00 de Brasília (nova tentativa de hora em hora se falhar), uma ao sair (pula se houver uma de menos de
/// 30 min; no máximo 60 s) e uma manual. Nunca lança: o resultado, bom ou ruim, vai para o log do painel e para <c>standby_last_sync_json</c>.
/// </summary>
public sealed class BackupService
{
    public const string UrlSecret = "backup_database_url";
    public const string StandbyEnv = "DATABASE_URL_STANDBY";
    public const int SyncHour = 4;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);
    public static readonly TimeSpan ExitMinGap = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    private readonly Database _db;
    private readonly Func<string> _url;
    private readonly Func<string, Action<string>, CancellationToken, Task<Dictionary<string, int>>> _copy;
    private readonly Func<DateTime> _brasiliaNow;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly EventLogStore _log;
    private readonly SettingsStore _settings;
    private string _handledDay = "";
    private DateTime _retryAfterUtc = DateTime.MinValue;

    /// <param name="url">Destino (padrão: <c>DATABASE_URL_STANDBY</c> e depois o segredo <c>backup_database_url</c>).</param>
    /// <param name="copy">Faz a cópia (padrão: <see cref="DbCopy.CopyLocalAsync"/>); os testes injetam uma falsa.</param>
    public BackupService(Database db, Func<string>? url = null, Func<string, Action<string>, CancellationToken, Task<Dictionary<string, int>>>? copy = null,
        Func<DateTime>? brasiliaNow = null, Func<DateTime>? utcNow = null)
    {
        _db = db;
        _url = url ?? DefaultUrl;
        _copy = copy ?? ((target, log, ct) => DbCopy.CopyLocalAsync(db, target, log, ct));
        _brasiliaNow = brasiliaNow ?? BrDates.BrasiliaNow;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = new EventLogStore(db);
        _settings = new SettingsStore(db);
    }

    public static string DefaultUrl()
    {
        var fromEnv = (Environment.GetEnvironmentVariable(StandbyEnv) ?? "").Trim();
        if (fromEnv.Length > 0) return fromEnv;
        if ((Environment.GetEnvironmentVariable("ODRADEK_DATA_DIR") ?? "").Trim().Length > 0) return ""; // pasta de dados de teste: nunca grava no backup real do cofre
        try { return SecretStore.Get(UrlSecret).Trim(); } catch (Exception) { return ""; }
    }

    public bool Configured => _url().Trim().Length > 0;

    /// <summary>O último resultado gravado (ou <c>null</c>).</summary>
    public BackupResult? Last()
    {
        try
        {
            if (JsonNode.Parse(_settings.Get("standby_last_sync_json", "null")) is not JsonObject last) return null;
            var tables = new Dictionary<string, int>();
            if (last["tables"] is JsonObject t)
                foreach (var (name, value) in t) tables[name] = int.TryParse(value?.ToJsonString(), out var n) ? n : 0;
            return new BackupResult(last["ok"]?.ToJsonString() == "true", last["at"]?.GetValue<string>() ?? "", last["error"]?.GetValue<string>() ?? "",
                tables, long.TryParse(last["rows"]?.ToJsonString(), out var rows) ? rows : 0,
                double.TryParse(last["seconds"]?.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>Há um backup bem-sucedido de menos de <paramref name="gap"/>?</summary>
    public bool IsRecent(TimeSpan gap)
    {
        var last = Last();
        return last is { Ok: true } && Clock.TryParse(last.At, out var when) && _utcNow() - when < gap;
    }

    /// <summary>Faz o backup agora. <c>null</c> = sem URL configurada. Só uma cópia por vez: a segunda espera a primeira.</summary>
    public async Task<BackupResult?> RunAsync(CancellationToken ct = default)
    {
        var target = _url().Trim();
        if (target.Length == 0) return null;
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var started = Stopwatch.StartNew();
            BackupResult result;
            try
            {
                if (!PostgresUrl.IsPostgres(target)) throw new BackupException("A URL do banco de backup deve começar com postgresql://.");
                var counts = await _copy(target, _ => { }, ct).ConfigureAwait(false);
                result = new BackupResult(true, Clock.ToIso(_utcNow()), "", counts, counts.Values.Sum(v => (long)v), started.Elapsed.TotalSeconds);
                Log("success", $"Standby atualizado: {result.Rows} linha(s) em {Math.Round(result.Seconds, 1).ToString("0.0", CultureInfo.InvariantCulture)}s.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                result = new BackupResult(false, Clock.ToIso(_utcNow()), Describe(ex), new Dictionary<string, int>(), 0, 0);
                Log("error", $"Falha ao atualizar o standby: {result.Error}");
            }
            try { _settings.Set("standby_last_sync_json", PyJson.Serialize(result.ToJson())); } catch (Exception) { /* a tela só deixa de mostrar */ }
            return result;
        }
        finally { _running.Release(); }
    }

    private void Log(string level, string message)
    {
        try { _log.Add(level, "standby", message); } catch (Exception) { /* o log nunca derruba o backup */ }
    }

    /// <summary>Texto de erro sem host, usuário nem senha da conexão (os drivers costumam incluí-los): <c>describe_db_error</c> do Python.</summary>
    public static string Describe(Exception exception)
    {
        if (exception is BackupException known) return known.Message;
        var inner = exception;
        var text = new System.Text.StringBuilder();
        for (var e = exception; e is not null; e = e.InnerException) { text.Append(e.Message).Append(' '); inner = e; }
        var lower = text.ToString().ToLowerInvariant();
        var network = exception is NpgsqlException or SocketException or TimeoutException || inner is SocketException or TimeoutException;
        if (!network) return exception.GetType().Name;
        if (new[] { "quota", "compute time", "data transfer" }.Any(lower.Contains)) return "Banco de dados indisponível: a cota do Neon (compute ou transferência) parece esgotada.";
        if (lower.Contains("password authentication failed") || lower.Contains("authentication failed") || lower.Contains("28p01")) return "Banco de dados indisponível: usuário ou senha da URL rejeitados.";
        if (lower.Contains("too many connections") || lower.Contains("remaining connection slots")) return "Banco de dados indisponível: limite de conexões atingido.";
        if (lower.Contains("timeout") || lower.Contains("timed out")) return "Banco de dados indisponível: tempo esgotado ao conectar.";
        if (lower.Contains("no such host") || lower.Contains("name or service not known") || lower.Contains("nodename") || lower.Contains("host desconhecido")
            || (inner as SocketException)?.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain)
            return "Banco de dados indisponível: o host da URL não foi encontrado.";
        return "Banco de dados indisponível: não foi possível conectar.";
    }

    // ---------------------------------------------------------------- agendamento

    private bool Claim(string today)
    {
        using var connection = _db.Open();
        return connection.Execute("UPDATE settings SET value=@today WHERE key='standby_sync_last_daily' AND value < @today", new { today }) == 1;
    }

    private void Release(string today)
    {
        using var connection = _db.Open();
        connection.Execute("UPDATE settings SET value='' WHERE key='standby_sync_last_daily' AND value=@today", new { today });
    }

    /// <summary>Uma passada: a cópia do dia, depois das 04:00 de Brasília, reivindicada de forma atômica (duas instâncias não copiam em dobro).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!Configured) return;
        var now = _brasiliaNow();
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (now.Hour < SyncHour || today == _handledDay || _utcNow() < _retryAfterUtc) return;
        if (!Claim(today)) { _handledDay = today; return; } // outra instância (ou execução anterior) já cuidou de hoje
        BackupResult? result;
        try { result = await RunAsync(ct).ConfigureAwait(false); }
        catch (Exception) { Release(today); _retryAfterUtc = _utcNow() + RetryDelay; throw; }
        if (result is { Ok: true }) { _handledDay = today; return; }
        Release(today); // libera a reivindicação para a próxima tentativa
        _retryAfterUtc = _utcNow() + RetryDelay;
    }

    /// <summary>Repete a passada a cada 10 minutos (como o Python) até o cancelamento; uma falha não derruba o laço.</summary>
    public async Task RunLoopAsync(CancellationToken ct, TimeSpan? interval = null)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _retryAfterUtc = _utcNow() + RetryDelay;
                Log("warning", $"Falha no agendador do standby: {Describe(ex)}");
            }
            try { await Task.Delay(interval ?? TimeSpan.FromMinutes(10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Backup ao sair: pula sem URL ou se o último tem menos de 30 min; espera no máximo 60 s e abandona a cópia depois disso.</summary>
    public async Task<BackupResult?> BackupOnExitAsync(TimeSpan? timeout = null)
    {
        if (!Configured || IsRecent(ExitMinGap)) return null;
        using var cts = new CancellationTokenSource(timeout ?? ExitTimeout);
        try { return await RunAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Log("warning", "Backup ao sair interrompido: passou do limite de tempo.");
            return null;
        }
    }
}
