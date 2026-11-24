using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Data;

namespace Odradek.Core.Queue;

/// <summary>Falha que a fila marca como definitiva na hora, sem tentar de novo.</summary>
public sealed class NonRetryableException(string message) : Exception(message);

/// <summary>Um item da fila entregue ao handler: o payload sem as chaves internas mais a função de progresso.</summary>
public sealed record QueueJob(long Id, string Kind, JsonObject Payload, Action<string> Progress);

public delegate Task<string> QueueHandler(QueueJob job, CancellationToken ct);

/// <summary>
/// Ponte entre as ações do painel e o motor em execução: quem está rodando agora (para não reenfileirar por cima nem
/// ficar sem cancelar) e o aviso para acordar o dispatcher. Há um motor por processo.
/// </summary>
public static class QueueRuntime
{
    internal static readonly ConcurrentDictionary<(Database Db, long Id), CancellationTokenSource> Running = new();
    public static event Action<Database>? WakeRequested;
    public static bool IsRunning(Database db, long id) => Running.ContainsKey((db, id));
    public static IReadOnlyCollection<long> RunningIds(Database db) => Running.Keys.Where(k => ReferenceEquals(k.Db, db)).Select(k => k.Id).ToArray();
    public static void Wake(Database db) => WakeRequested?.Invoke(db);

    /// <summary>Interrompe, se estiver rodando aqui, o handler do item cancelado (as chamadas de IA/HTTP respeitam o token).</summary>
    public static void Interrupt(Database db, long id)
    {
        if (Running.TryGetValue((db, id), out var source))
            try { source.Cancel(); } catch (ObjectDisposedException) { }
    }
}

public sealed class QueueEngineOptions
{
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    /// <summary>Teto do sono do dispatcher: só uma rede de segurança, pois enqueue, retry e o fim de um job o acordam.</summary>
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan MinWait { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PauseMin { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PauseMax { get; init; } = TimeSpan.FromSeconds(600);
    public TimeSpan ProgressFlush { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan WorkersCache { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ErrorBackoffMax { get; init; } = TimeSpan.FromSeconds(300);
}

/// <summary>
/// Fila persistente com workers paralelos e repetição para operações de IA (porte de <c>JobQueue</c>, <c>jobs/job_queue.py</c>).
/// O estado fica na tabela <c>queue_jobs</c> (o mesmo do app Python); o motor só reivindica, executa e atualiza.
/// </summary>
public sealed partial class QueueEngine : IDisposable
{
    public const string KindResume = "resume_analysis";
    public const string KindApply = "job_apply";
    public const string KindWorthReverify = "worth_reverify";
    public const int BackoffCapSeconds = 20 * 60;

    private readonly Database _db;
    private readonly IReadOnlyDictionary<string, QueueHandler> _handlers;
    private readonly QueueEngineOptions _options;
    private readonly EventLogStore _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Dictionary<string, DateTime> _pausedUntil = [];
    private readonly Dictionary<long, List<string>> _progress = [];
    private readonly Dictionary<long, DateTime> _progressFlushedAt = [];
    private readonly Dictionary<long, Task> _inflight = [];
    private CancellationTokenSource? _stop;
    private Task? _dispatcher;
    private string? _nextDue;

    public QueueEngine(Database db, IReadOnlyDictionary<string, QueueHandler> handlers, QueueEngineOptions? options = null)
    {
        _db = db;
        _handlers = handlers;
        _options = options ?? new QueueEngineOptions();
        _log = new EventLogStore(db);
        QueueRuntime.WakeRequested += OnWakeRequested;
    }

    private DateTime Now => _options.UtcNow();
    private static string Iso(DateTime utc) => Clock.ToIso(utc);
    public void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }
    private void OnWakeRequested(Database db) { if (ReferenceEquals(db, _db)) Wake(); }

    // ---------------------------------------------------------------- política de repetição

    /// <summary>Backoff exponencial com jitter por item (espalha repetições sincronizadas). Teto de 20 minutos.</summary>
    public static int BackoffSeconds(int attempt, bool rateLimited = false)
    {
        var floor = rateLimited ? 60 : 20;
        var shift = Math.Min(Math.Max(0, attempt - 1), 20);
        var baseSeconds = Math.Min(BackoffCapSeconds, floor * (1L << shift));
        var low = floor;
        var high = (int)Math.Min(BackoffCapSeconds, Math.Max(low + 15, (long)(baseSeconds * 1.75)));
        return Random.Shared.Next(low, high + 1);
    }

    public static bool IsRateLimitError(Exception exc)
    {
        var message = exc.Message.ToLowerInvariant();
        return new[] { "429", "rate limit", "quota", "resource_exhausted", "resource exhausted", "too many requests" }.Any(message.Contains);
    }

    public static bool IsRetryableError(Exception exc)
    {
        var name = exc.GetType().Name.ToLowerInvariant();
        if (name.Contains("nonretryable")) return false;
        if (name.Contains("aiunavailable") || name.Contains("airesponse")) return true;
        var message = exc.Message.ToLowerInvariant();
        if (exc is SqliteException && message.Contains("locked")) return true;
        // Passo que exige uma pessoa (login, captcha) nunca é repetido sozinho.
        if (new[] { "login", "checkpoint", "captcha", "authwall", "perfil chrome", "manual" }.Any(message.Contains)) return false;
        return new[]
        {
            "429", "503", "502", "504", "quota", "rate limit", "resource_exhausted", "unavailable", "overload", "temporar", "try again",
            "retry", "high demand", "ia indisponível", "resource exhausted", "database is locked", "database locked",
        }.Any(message.Contains);
    }

    /// <summary>"AiUnavailableException" vira "AiUnavailableError": mantém o texto que o histórico do app Python já mostra.</summary>
    private static string Describe(Exception exc) => $"{exc.GetType().Name.Replace("Exception", "Error")}: {exc.Message}";

    // ---------------------------------------------------------------- ciclo de vida

    public void Start()
    {
        lock (_gate)
        {
            if (_dispatcher is { IsCompleted: false }) return;
            _stop = new CancellationTokenSource();
            var (workers, _, ttl) = QueueLimits.Read(_db);
            var recovered = RecoverStaleRunning();
            var respread = RespreadRetryWaits();
            var message = $"Fila iniciada com até {workers} worker(s).";
            if (recovered > 0) message += $" {recovered} job(s) interrompido(s) recolocados como pendentes.";
            if (respread > 0) message += $" {respread} retry(s) reespalhado(s) com jitter.";
            _ = ttl;
            _log.Add("info", "queue", message);
            var token = _stop.Token;
            _dispatcher = Task.Run(() => DispatchLoopAsync(token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? stop;
        Task? dispatcher;
        lock (_gate) { stop = _stop; dispatcher = _dispatcher; _stop = null; _dispatcher = null; }
        if (stop is null) return;
        stop.Cancel();
        Wake();
        try { dispatcher?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _log.Add("info", "queue", "Fila parada.");
        stop.Dispose();
    }

    public void Dispose()
    {
        QueueRuntime.WakeRequested -= OnWakeRequested;
        Stop();
    }

    /// <summary>Itens "running" sem processo vivo (reinício) voltam a pendente; os da candidatura legada pelo navegador são cancelados.</summary>
    public int RecoverStaleRunning()
    {
        var now = Now;
        var (_, _, ttlHours) = QueueLimits.Read(_db);
        using var connection = _db.Open();
        var legacy = connection.Execute(
            @"UPDATE queue_jobs SET status='cancelled', updated_at=@now,
                last_error='Candidatura pelo navegador removida; use a extensão no Chrome para formulários.'
              WHERE kind='linkedin_apply' AND status IN ('pending','running','retry_wait')", new { now = Iso(now) });
        var requeued = connection.Execute(
            @"UPDATE queue_jobs SET status='pending', next_run_at=@now, expires_at=@expires, updated_at=@now,
                last_error=CASE WHEN last_error='' THEN 'Interrompido (reinício do app); reenfileirado.' ELSE last_error END
              WHERE status='running'", new { now = Iso(now), expires = Iso(now.AddHours(ttlHours)) });
        return requeued + legacy;
    }

    /// <summary>Reespalha o horário dos itens em espera para quebrar a sincronia (por exemplo, 429 em massa).</summary>
    public int RespreadRetryWaits()
    {
        var now = Now;
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        var rows = connection.Query<(long Id, long Attempts, string? LastError)>("SELECT id, attempts, last_error FROM queue_jobs WHERE status='retry_wait'", transaction: tx).ToList();
        foreach (var row in rows)
        {
            var delay = BackoffSeconds((int)Math.Max(1, row.Attempts), IsRateLimitError(new Exception(row.LastError ?? "")));
            connection.Execute("UPDATE queue_jobs SET next_run_at=@next, updated_at=@now WHERE id=@id AND status='retry_wait'",
                new { next = Iso(now.AddSeconds(delay)), now = Iso(now), id = row.Id }, tx);
        }
        tx.Commit();
        return rows.Count;
    }

    /// <summary>Retenção: itens terminados (com histórico de até 12 KB cada) não ficam para sempre na tabela.</summary>
    public int PurgeTerminalOlderThan(int days = 14)
    {
        using var connection = _db.Open();
        return connection.Execute("DELETE FROM queue_jobs WHERE status IN ('succeeded','failed','cancelled') AND updated_at < @cutoff",
            new { cutoff = Iso(Now.AddDays(-days)) });
    }

    public bool HasActiveKind(string kind)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<long?>("SELECT 1 FROM queue_jobs WHERE kind=@kind AND status IN ('pending','running','retry_wait') LIMIT 1", new { kind }) is not null;
    }

    // ---------------------------------------------------------------- pausa por tipo (cota da IA)

    private Dictionary<string, DateTime> ActivePauses()
    {
        var now = Now;
        lock (_gate)
        {
            foreach (var kind in _pausedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _pausedUntil.Remove(kind);
            return new Dictionary<string, DateTime>(_pausedUntil);
        }
    }

    private void PauseKind(string kind, double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        span = span < _options.PauseMin ? _options.PauseMin : span > _options.PauseMax ? _options.PauseMax : span;
        lock (_gate)
        {
            var until = Now + span;
            _pausedUntil[kind] = _pausedUntil.TryGetValue(kind, out var current) && current > until ? current : until;
        }
    }

    // ---------------------------------------------------------------- reivindicação e despacho

    private sealed record Claimed(long Id, string Kind, string Payload, long Attempts, long MaxAttempts, string? ExpiresAt);

    private List<Claimed> ClaimBatch(int limit)
    {
        var now = Iso(Now);
        var paused = ActivePauses().Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var skip = paused.Count > 0 ? $" AND kind NOT IN ({string.Join(",", paused.Select((_, i) => $"@p{i}"))})" : "";
        var args = new DynamicParameters(new { now, limit });
        for (var i = 0; i < paused.Count; i++) args.Add($"p{i}", paused[i]);
        var claimed = new List<Claimed>();
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        connection.Execute("UPDATE queue_jobs SET status='failed', last_error='Expirado (TTL da fila)', updated_at=@now WHERE status IN ('pending','retry_wait') AND expires_at < @now", args, tx);
        var rows = connection.Query<Claimed>(
            $@"SELECT id AS Id, kind AS Kind, payload AS Payload, attempts AS Attempts, max_attempts AS MaxAttempts, expires_at AS ExpiresAt
               FROM queue_jobs WHERE status IN ('pending','retry_wait') AND next_run_at <= @now{skip} ORDER BY id ASC LIMIT @limit", args, tx).ToList();
        // Uma falha de cota que terminou depois do cálculo das pausas (acima) e antes deste SELECT não pode deixar passar um item do tipo recém-pausado.
        var pausedNow = ActivePauses().Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (claimed.Count >= limit) break;
            if (pausedNow.Contains(row.Kind)) continue;
            if (connection.Execute("UPDATE queue_jobs SET status='running', updated_at=@now WHERE id=@id AND status IN ('pending','retry_wait')", new { now, id = row.Id }, tx) > 0)
                claimed.Add(row);
        }
        _nextDue = connection.ExecuteScalar<string?>($"SELECT MIN(next_run_at) FROM queue_jobs WHERE status IN ('pending','retry_wait'){skip}", args, tx);
        tx.Commit();
        return claimed;
    }

    /// <summary>Quanto o dispatcher pode dormir: até o próximo item agendado ou o fim de uma pausa, no máximo <see cref="QueueEngineOptions.IdlePoll"/>.</summary>
    private TimeSpan IdleWait()
    {
        var wait = _options.IdlePoll;
        if (Clock.TryParse(_nextDue, out var due)) wait = Min(wait, due - Now + TimeSpan.FromSeconds(0.5));
        var pauses = ActivePauses();
        if (pauses.Count > 0) wait = Min(wait, pauses.Values.Min() - Now + TimeSpan.FromSeconds(0.5));
        return wait < _options.MinWait ? _options.MinWait : wait;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private async Task DispatchLoopAsync(CancellationToken stop)
    {
        var workers = 3;
        var workersReadAt = DateTime.MinValue;
        var errorDelay = TimeSpan.Zero;
        while (!stop.IsCancellationRequested)
        {
            while (_wake.CurrentCount > 0) _wake.Wait(0); // antes de olhar o banco: um enqueue durante o corpo do laço não se perde
            var delay = _options.IdlePoll;
            try
            {
                if (Now - workersReadAt >= _options.WorkersCache)
                {
                    workers = QueueLimits.Read(_db).Workers;
                    workersReadAt = Now;
                }
                int capacity;
                lock (_gate)
                {
                    foreach (var id in _inflight.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList()) _inflight.Remove(id);
                    capacity = Math.Max(0, workers - _inflight.Count);
                }
                if (capacity > 0)
                {
                    foreach (var row in ClaimBatch(capacity))
                    {
                        var source = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        QueueRuntime.Running[(_db, row.Id)] = source;
                        var task = Task.Run(() => RunJobAsync(row, source));
                        lock (_gate) _inflight[row.Id] = task;
                    }
                }
                delay = IdleWait();
                errorDelay = TimeSpan.Zero;
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                workersReadAt = DateTime.MinValue;
                errorDelay = TimeSpan.FromSeconds(Math.Min(_options.ErrorBackoffMax.TotalSeconds, Math.Max(5, errorDelay.TotalSeconds * 2)));
                delay = errorDelay;
                try { _log.Add("error", "queue", $"Falha no dispatcher da fila (nova tentativa em {delay.TotalSeconds:0}s)."); } catch (Exception) { }
            }
            try { await _wake.WaitAsync(delay, stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Espera os itens em execução terminarem (testes e encerramento ordenado).</summary>
    public async Task WaitIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            Task[] tasks;
            lock (_gate) tasks = _inflight.Values.Where(t => !t.IsCompleted).ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(50));
        }
    }

    // ---------------------------------------------------------------- execução de um item

    private async Task RunJobAsync(Claimed row, CancellationTokenSource source)
    {
        try { await RunJobInnerAsync(row, source.Token); }
        catch (Exception) { /* uma falha inesperada de banco não derruba o worker; o item volta pela recuperação do próximo boot */ }
        finally
        {
            try { FlushProgress(row.Id); } catch (Exception) { }
            lock (_gate) { _progress.Remove(row.Id); _progressFlushedAt.Remove(row.Id); }
            QueueRuntime.Running.TryRemove((_db, row.Id), out _);
            source.Dispose();
            lock (_gate) _inflight.Remove(row.Id); // libera a vaga antes de acordar o dispatcher
            _db.Bus.Publish(new QueueChanged(row.Id));
            Wake(); // há capacidade livre: o próximo item pode começar já
        }
    }

    private bool IsCancelled(long id)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<string?>("SELECT status FROM queue_jobs WHERE id=@id", new { id }) == "cancelled";
    }

    private async Task RunJobInnerAsync(Claimed row, CancellationToken ct)
    {
        var id = row.Id;
        var kind = row.Kind;
        var attempts = (int)row.Attempts + 1;
        var maxAttempts = (int)row.MaxAttempts;
        var now = Now;
        JsonObject payload;
        try { payload = JsonNode.Parse(string.IsNullOrEmpty(row.Payload) ? "{}" : row.Payload) as JsonObject ?? new JsonObject { ["_raw"] = JsonNode.Parse(row.Payload) }; }
        catch (JsonException) { payload = []; }
        payload["_queue_job_id"] = id;
        if (IsCancelled(id))
        {
            _log.Add("warning", "queue", $"Job #{id} ({kind}) já cancelado; não executa.");
            return;
        }
        if (!_handlers.TryGetValue(kind, out var handler))
        {
            Finish(id, "failed", attempts, $"Handler ausente para {kind}", "");
            return;
        }
        AppendProgress(id, attempts > 1 ? $"— retry {attempts}/{maxAttempts} —" : $"iniciando ({kind})");
        _log.Add("info", "queue", $"Executando #{id} ({kind}), tentativa {attempts}/{maxAttempts}.");
        try
        {
            var result = await handler(new QueueJob(id, kind, payload, message => { try { AppendProgress(id, message); } catch (Exception) { } }), ct);
            if (string.IsNullOrEmpty(result)) result = "ok";
            AppendProgress(id, $"concluído: {Text.Take(result, 200)}", flush: true);
            Finish(id, "succeeded", attempts, "", Text.Take(result, 1000));
            _log.Add("success", "queue", $"Job #{id} ({kind}) concluído.");
        }
        catch (Exception exc)
        {
            if (ct.IsCancellationRequested && exc is OperationCanceledException || IsCancelled(id))
            {
                if (IsCancelled(id)) _log.Add("warning", "queue", $"Job #{id} ({kind}) cancelado durante execução.");
                return; // cancelado ou app encerrando: um item "running" volta como pendente no próximo boot
            }
            var error = Describe(exc);
            var retryable = IsRetryableError(exc);
            var expires = Clock.TryParse(row.ExpiresAt, out var parsed) ? parsed : (DateTime?)null;
            if (retryable && attempts < maxAttempts && (expires is null || now < expires))
            {
                var delay = BackoffSeconds(attempts, IsRateLimitError(exc));
                if (IsRateLimitError(exc) || exc.GetType().Name.Contains("AiUnavailable", StringComparison.OrdinalIgnoreCase))
                    PauseKind(kind, delay); // a IA está sem cota ou fora: o resto da fila não falha em série
                AppendProgress(id, $"retry em ~{delay}s: {Text.Take(error, 180)}", flush: true);
                using (var connection = _db.Open())
                    connection.Execute("UPDATE queue_jobs SET status='retry_wait', attempts=@attempts, next_run_at=@next, last_error=@error, updated_at=@now WHERE id=@id AND status='running'",
                        new { attempts, next = Iso(now.AddSeconds(delay)), error = Text.Take(error, 1000), now = Iso(now), id });
                _log.Add("warning", "queue", $"Job #{id} ({kind}) em retry ({attempts}/{maxAttempts}) em ~{delay}s: {error}");
            }
            else
            {
                AppendProgress(id, $"falhou: {Text.Take(error, 200)}", flush: true);
                Finish(id, "failed", attempts, Text.Take(error, 1000), "");
                _log.Add("error", "queue", $"Job #{id} ({kind}) falhou em definitivo: {error}");
            }
        }
    }

    private void Finish(long id, string status, int attempts, string lastError, string result)
    {
        using var connection = _db.Open();
        // Não sobrescreve um cancelamento manual feito durante a execução.
        connection.Execute("UPDATE queue_jobs SET status=@status, attempts=@attempts, last_error=@lastError, result=@result, updated_at=@now WHERE id=@id AND status='running'",
            new { status, attempts, lastError, result, now = Iso(Now), id });
    }

    // ---------------------------------------------------------------- progresso em lotes

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Acrescenta uma linha ao histórico do item. As linhas ficam em memória e saem juntas a cada poucos segundos (ou já, com <paramref name="flush"/>).</summary>
    public void AppendProgress(long id, string message, int maxChars = 12000, bool flush = false)
    {
        var text = Whitespace().Replace((message ?? "").Trim(), " ");
        if (text.Length == 0) return;
        var line = $"{Now:HH:mm:ss} {Text.Take(text, 1000)}";
        bool due;
        lock (_gate)
        {
            if (!_progress.TryGetValue(id, out var lines)) _progress[id] = lines = [];
            lines.Add(line);
            due = flush || !_progressFlushedAt.TryGetValue(id, out var last) || Now - last >= _options.ProgressFlush;
        }
        if (due) FlushProgress(id, maxChars);
    }

    public void FlushProgress(long id, int maxChars = 12000)
    {
        List<string> lines;
        lock (_gate)
        {
            if (!_progress.Remove(id, out lines!)) lines = [];
            _progressFlushedAt[id] = Now;
        }
        if (lines.Count == 0) return;
        try
        {
            using (var connection = _db.Open())
            {
                var previous = connection.ExecuteScalar<string?>("SELECT progress_log FROM queue_jobs WHERE id=@id", new { id });
                if (previous is null && !connection.ExecuteScalar<bool>("SELECT EXISTS(SELECT 1 FROM queue_jobs WHERE id=@id)", new { id })) return;
                var merged = ((previous ?? "") + (string.IsNullOrEmpty(previous) ? "" : "\n") + string.Join("\n", lines)).Trim();
                if (merged.Length > maxChars)
                {
                    merged = Text.TakeLast(merged, maxChars);
                    var newline = merged.IndexOf('\n');
                    if (newline == 0) merged = merged[1..]; // o corte caiu exatamente entre duas linhas
                    else if (newline > 0) merged = merged[(newline + 1)..]; // não deixa a primeira linha cortada ao meio
                }
                connection.Execute("UPDATE queue_jobs SET progress_log=@merged, updated_at=@now WHERE id=@id", new { merged, now = Iso(Now), id });
            }
            _db.Bus.Publish(new QueueChanged(id));
        }
        catch (Exception)
        {
            lock (_gate) // banco indisponível: guarda as linhas para a próxima tentativa
                _progress[id] = [.. lines, .. _progress.GetValueOrDefault(id) ?? []];
            throw;
        }
    }
}
