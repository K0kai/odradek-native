using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Data;
using Odradek.Core.Queue;

namespace Odradek.Core.Tests;

public class QueueEngineTests
{
    private static async Task WaitFor(Func<bool> condition, int seconds = 8)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("a condição não ficou verdadeira a tempo");
    }

    private static Dictionary<string, QueueHandler> Handlers(QueueHandler apply, QueueHandler? resume = null) => new()
    {
        [QueueEngine.KindApply] = apply,
        [QueueEngine.KindResume] = resume ?? ((_, _) => Task.FromResult("resume ok")),
        [QueueEngine.KindWorthReverify] = (_, _) => Task.FromResult("verificado"),
    };

    private static string Status(TempDb t, long id) => t.Scalar<string>("SELECT status FROM queue_jobs WHERE id=@id", new { id });
    private static long Enqueue(TempDb t, string kind = "job_apply", long jobId = 1) =>
        new QueueActions(t.Db).Enqueue(kind, [new("job_id", jobId)]);

    [Fact]
    public async Task Runs_a_pending_item_records_result_progress_and_publishes_changes()
    {
        using var t = new TempDb();
        using var engine = new QueueEngine(t.Db, Handlers((job, _) =>
        {
            job.Progress("avaliando   com\n gemini");
            Assert.Equal(7, job.Payload["job_id"]!.GetValue<long>());
            Assert.Equal(job.Id, job.Payload["_queue_job_id"]!.GetValue<long>());
            return Task.FromResult("nota 80");
        }));
        engine.Start();
        var id = Enqueue(t, jobId: 7);
        await WaitFor(() => Status(t, id) == "succeeded");
        Assert.Equal(1, t.Scalar<int>("SELECT attempts FROM queue_jobs WHERE id=@id", new { id }));
        Assert.Equal("nota 80", t.Scalar<string>("SELECT result FROM queue_jobs WHERE id=@id", new { id }));
        var progress = t.Scalar<string>("SELECT progress_log FROM queue_jobs WHERE id=@id", new { id });
        Assert.Matches(@"^\d\d:\d\d:\d\d iniciando \(job_apply\)\n\d\d:\d\d:\d\d avaliando com gemini\n\d\d:\d\d:\d\d concluído: nota 80$", progress);
        Assert.Contains(t.Changes, c => c is QueueChanged);
        Assert.Contains(t.Scalar<string>("SELECT group_concat(message, '|') FROM event_logs").Split('|'), m => m == $"Job #{id} (job_apply) concluído.");
    }

    [Fact]
    public async Task Ai_outage_goes_to_retry_wait_with_backoff_and_pauses_the_kind_but_not_others()
    {
        using var t = new TempDb();
        var secondRan = false;
        using var engine = new QueueEngine(t.Db, Handlers(
            (_, _) => throw new AiUnavailableException("IA indisponível: quota exceeded"),
            (_, _) => { secondRan = true; return Task.FromResult("ok"); }));
        engine.Start();
        var first = Enqueue(t, "job_apply", 1);
        await WaitFor(() => Status(t, first) == "retry_wait");
        Assert.Equal(1, t.Scalar<int>("SELECT attempts FROM queue_jobs WHERE id=@first", new { first }));
        Assert.StartsWith("AiUnavailableError: IA indisponível", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@first", new { first }));
        var next = DateTime.Parse(t.Scalar<string>("SELECT next_run_at FROM queue_jobs WHERE id=@first", new { first }), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange((next - DateTime.UtcNow).TotalSeconds, 55, 190); // cota (429/quota): piso de 60 s
        // o mesmo tipo fica em pausa; outro tipo segue normalmente
        var sameKind = Enqueue(t, "job_apply", 2);
        var otherKind = Enqueue(t, "resume_analysis", 3);
        await WaitFor(() => Status(t, otherKind) == "succeeded");
        Assert.True(secondRan);
        await Task.Delay(300);
        Assert.Equal("pending", Status(t, sameKind));
    }

    [Fact]
    public async Task Non_retryable_and_unrecognized_errors_fail_immediately_and_exhausted_attempts_fail()
    {
        using var t = new TempDb();
        var calls = 0;
        using var engine = new QueueEngine(t.Db, Handlers((job, _) =>
        {
            Interlocked.Increment(ref calls);
            throw job.Payload["job_id"]!.GetValue<long>() switch
            {
                1 => new NonRetryableException("vaga inválida"),
                2 => new InvalidOperationException("erro qualquer"),
                _ => new AiResponseException("JSON inutilizável"),
            };
        }));
        engine.Start();
        var a = Enqueue(t, jobId: 1);
        var b = Enqueue(t, jobId: 2);
        await WaitFor(() => Status(t, a) == "failed" && Status(t, b) == "failed");
        Assert.Equal("NonRetryableError: vaga inválida", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@a", new { a }));
        Assert.Equal("InvalidOperationError: erro qualquer", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@b", new { b }));
        Assert.Equal(2, calls);

        new SettingsStore(t.Db).Set("queue_max_attempts", "1");
        var c = Enqueue(t, jobId: 3); // repetível, mas só 1 tentativa permitida
        await WaitFor(() => Status(t, c) == "failed");
        Assert.Equal("AiResponseError: JSON inutilizável", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@c", new { c }));
    }

    [Fact]
    public async Task Parallelism_follows_queue_max_workers()
    {
        using var t = new TempDb();
        new SettingsStore(t.Db).Set("queue_max_workers", "2");
        var current = 0;
        var peak = 0;
        var done = 0;
        var gate = new SemaphoreSlim(0);
        using var engine = new QueueEngine(t.Db, Handlers(async (_, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            lock (gate) peak = Math.Max(peak, now);
            await gate.WaitAsync(ct);
            Interlocked.Decrement(ref current);
            Interlocked.Increment(ref done);
            return "ok";
        }));
        for (var i = 1; i <= 5; i++) Enqueue(t, jobId: i);
        engine.Start();
        await WaitFor(() => Volatile.Read(ref current) == 2);
        await Task.Delay(200);
        Assert.Equal(2, current); // nem um a mais enquanto os dois estão ocupados
        gate.Release(5);
        await WaitFor(() => Volatile.Read(ref done) == 5);
        Assert.Equal(2, peak);
        Assert.Equal(5, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs WHERE status='succeeded'"));
    }

    [Fact]
    public async Task Expired_items_fail_with_the_ttl_message_without_running()
    {
        using var t = new TempDb();
        var ran = false;
        using var engine = new QueueEngine(t.Db, Handlers((_, _) => { ran = true; return Task.FromResult("x"); }));
        var id = Enqueue(t);
        t.Db.Open().Execute("UPDATE queue_jobs SET expires_at='2000-01-01T00:00:00+00:00' WHERE id=@id", new { id });
        engine.Start();
        await WaitFor(() => Status(t, id) == "failed");
        Assert.Equal("Expirado (TTL da fila)", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@id", new { id }));
        Assert.False(ran);
    }

    [Fact]
    public async Task Start_requeues_interrupted_items_and_cancels_the_legacy_browser_kind()
    {
        using var t = new TempDb();
        var interrupted = Enqueue(t);
        using (var c = t.Db.Open())
        {
            c.Execute("UPDATE queue_jobs SET status='running' WHERE id=@interrupted", new { interrupted });
            c.Execute("INSERT INTO queue_jobs(kind,payload,status,next_run_at,expires_at,created_at,updated_at) VALUES('linkedin_apply','{}','pending','x','x','x','x')");
        }
        var release = new TaskCompletionSource();
        using var engine = new QueueEngine(t.Db, Handlers(async (_, _) => { await release.Task; return "ok"; }));
        engine.Start();
        await WaitFor(() => Status(t, interrupted) == "running"); // foi recolocado e reivindicado de novo
        Assert.Equal("Interrompido (reinício do app); reenfileirado.", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@interrupted", new { interrupted }));
        Assert.Equal("cancelled", t.Scalar<string>("SELECT status FROM queue_jobs WHERE kind='linkedin_apply'"));
        Assert.Contains("2 job(s) interrompido(s) recolocados como pendentes", t.Scalar<string>("SELECT group_concat(message, '|') FROM event_logs WHERE message LIKE 'Fila iniciada%'"));
        release.SetResult();
    }

    [Fact]
    public async Task Cancelling_a_running_item_aborts_its_handler_and_keeps_it_cancelled()
    {
        using var t = new TempDb();
        var started = new TaskCompletionSource();
        var aborted = false;
        using var engine = new QueueEngine(t.Db, Handlers(async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { aborted = true; throw; }
            return "nunca";
        }));
        engine.Start();
        var id = Enqueue(t);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(new QueueActions(t.Db).RetryNow(id)); // rodando neste processo: não reenfileira por cima
        Assert.Equal(0, new QueueActions(t.Db).RetryAll());
        Assert.True(new QueueActions(t.Db).Cancel(id));
        await WaitFor(() => aborted);
        await engine.WaitIdleAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("cancelled", Status(t, id));
        Assert.Equal("Cancelado manualmente", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@id", new { id }));
        Assert.True(new QueueActions(t.Db).RetryNow(id)); // terminado: agora pode
    }

    [Fact]
    public async Task Enqueue_wakes_an_idle_dispatcher_and_a_missing_handler_fails_clearly()
    {
        using var t = new TempDb();
        using var engine = new QueueEngine(t.Db, new Dictionary<string, QueueHandler> { [QueueEngine.KindApply] = (_, _) => Task.FromResult("ok") });
        engine.Start();
        await Task.Delay(300); // dispatcher já dormiu: sem nada na fila
        var id = Enqueue(t);
        await WaitFor(() => Status(t, id) == "succeeded", seconds: 3);
        var orphan = Enqueue(t, "resume_analysis");
        await WaitFor(() => Status(t, orphan) == "failed", seconds: 3);
        Assert.Equal("Handler ausente para resume_analysis", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@orphan", new { orphan }));
    }

    [Fact]
    public void Respread_gives_waiting_items_new_times_and_purge_removes_only_old_terminal_ones()
    {
        using var t = new TempDb();
        using var engine = new QueueEngine(t.Db, Handlers((_, _) => Task.FromResult("")));
        var waiting = Enqueue(t);
        var oldDone = Enqueue(t, jobId: 2);
        var recentDone = Enqueue(t, jobId: 3);
        using (var c = t.Db.Open())
        {
            c.Execute("UPDATE queue_jobs SET status='retry_wait', attempts=2, last_error='429 quota', next_run_at='2000-01-01T00:00:00+00:00' WHERE id=@waiting", new { waiting });
            c.Execute("UPDATE queue_jobs SET status='succeeded', updated_at='2000-01-01T00:00:00+00:00' WHERE id=@oldDone", new { oldDone });
            c.Execute("UPDATE queue_jobs SET status='failed' WHERE id=@recentDone", new { recentDone });
        }
        Assert.Equal(1, engine.RespreadRetryWaits());
        var next = DateTime.Parse(t.Scalar<string>("SELECT next_run_at FROM queue_jobs WHERE id=@waiting", new { waiting }), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.True(next > DateTime.UtcNow.AddSeconds(55)); // 429 na tentativa 2: 60–210 s
        Assert.Equal(1, engine.PurgeTerminalOlderThan(14));
        Assert.Equal(2, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs"));
        Assert.True(engine.HasActiveKind("job_apply") is false || engine.HasActiveKind("job_apply")); // só garante que a consulta roda
    }

    [Fact]
    public async Task Progress_is_batched_and_trimmed_to_the_last_lines_without_cutting_one_in_half()
    {
        using var t = new TempDb();
        using var engine = new QueueEngine(t.Db, Handlers((_, _) => Task.FromResult("")));
        var id = Enqueue(t);
        for (var i = 0; i < 400; i++) engine.AppendProgress(id, $"linha {i} " + new string('x', 40));
        engine.FlushProgress(id);
        var log = t.Scalar<string>("SELECT progress_log FROM queue_jobs WHERE id=@id", new { id });
        var head = log[..Math.Min(80, log.Length)].Replace("\n", "|");
        Assert.True(log.Length <= 12000, $"len={log.Length} head=[{head}]");
        Assert.True(log.Length > 11000, $"len={log.Length} head=[{head}]");
        Assert.Matches(@"^\d\d:\d\d:\d\d linha \d+ x{40}", log.Split('\n')[0]); // primeira linha inteira
        Assert.EndsWith("linha 399 " + new string('x', 40), log);
        await Task.CompletedTask;
    }

    [Fact]
    public void Backoff_stays_in_range_and_error_classification_matches_python()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.InRange(QueueEngine.BackoffSeconds(1), 20, 35);
            Assert.InRange(QueueEngine.BackoffSeconds(1, true), 60, 105);
            Assert.InRange(QueueEngine.BackoffSeconds(3, true), 60, 420);
            Assert.InRange(QueueEngine.BackoffSeconds(40, true), 60, 1200);
            Assert.InRange(QueueEngine.BackoffSeconds(99), 20, 1200);
        }
        Assert.True(QueueEngine.IsRetryableError(new AiUnavailableException("x")));
        Assert.True(QueueEngine.IsRetryableError(new AiResponseException("x")));
        Assert.True(QueueEngine.IsRetryableError(new Exception("HTTP 503 high demand")));
        Assert.True(QueueEngine.IsRetryableError(new Microsoft.Data.Sqlite.SqliteException("database is locked", 5)));
        Assert.False(QueueEngine.IsRetryableError(new NonRetryableException("429 quota")));
        Assert.False(QueueEngine.IsRetryableError(new Exception("captcha 429")));
        Assert.False(QueueEngine.IsRetryableError(new ArgumentException("Idioma incerto")));
        Assert.True(QueueEngine.IsRateLimitError(new Exception("Too Many Requests")));
        Assert.False(QueueEngine.IsRateLimitError(new Exception("503")));
    }
}
