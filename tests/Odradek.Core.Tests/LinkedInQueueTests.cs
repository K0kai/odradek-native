using System.Net;
using Dapper;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

public class LinkedInQueueTests : IDisposable
{
    private const string Guest = """<h2 class="top-card-layout__title">Dev Pleno</h2><a class="topcard__org-name-link">Acme</a><span class="topcard__flavor--bullet">Brasil</span><span class="posted-time-ago__text">2 days ago</span><div class="show-more-less-html__markup">Descrição da vaga com requisitos.</div>""";
    private readonly TempDb _db = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), "odradek-queue-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly List<LinkedInQueue> _queues = [];

    public void Dispose()
    {
        foreach (var queue in _queues) queue.Dispose();
        try { File.Delete(_file); } catch (IOException) { /* arquivo ainda em uso: some com a pasta temporária */ }
        _db.Dispose();
    }

    /// <summary>Fila com internet e relógio falsos: <paramref name="statuses"/> responde, em ordem, a cada pedido ao LinkedIn (o último se repete).</summary>
    private LinkedInQueue Queue(List<(string Id, int Status)> requests, List<TimeSpan> waits, params int[] statuses)
    {
        var script = new Queue<int>(statuses);
        var queue = new LinkedInQueue(_db.Db, null, _file,
            fetch: (url, _) =>
            {
                var status = script.Count > 1 ? script.Dequeue() : script.Peek();
                requests.Add((url[(url.LastIndexOf('/') + 1)..], status));
                return Task.FromResult((status, status == 200 ? Guest : ""));
            },
            delay: (span, _) => { waits.Add(span); return Task.CompletedTask; },
            pace: () => TimeSpan.FromSeconds(5));
        _queues.Add(queue);
        return queue;
    }

    private long Jobs() => _db.Scalar<long>("SELECT COUNT(*) FROM jobs");

    [Fact]
    public async Task Queued_jobs_are_read_in_the_background_and_saved_as_they_arrive()
    {
        List<(string Id, int Status)> requests = [];
        List<TimeSpan> waits = [];
        var queue = Queue(requests, waits, 200);
        var result = queue.Enqueue(["4000000101", "4000000102", "4000000103", "4000000104", "4000000105"]);
        Assert.Equal(5, result["queued"]!.GetValue<int>());
        await queue.WhenIdle();

        Assert.Equal(5, Jobs());
        Assert.Equal(["4000000101", "4000000102", "4000000103", "4000000104", "4000000105"], requests.Select(r => r.Id));
        Assert.Equal(0, queue.Pending);
        var status = queue.Status();
        Assert.Equal((5, false), (status["inserted"]!.GetValue<int>(), status["running"]!.GetValue<bool>()));
        Assert.All(waits, wait => Assert.Equal(TimeSpan.FromSeconds(5), wait)); // só a pausa normal, nenhum 429
        Assert.False(File.Exists(_file) && File.ReadAllText(_file).Contains("4000000101")); // a fila gravada ficou vazia
    }

    [Fact]
    public async Task A_429_backs_off_with_growing_waits_and_keeps_the_job_for_the_retry()
    {
        List<(string Id, int Status)> requests = [];
        List<TimeSpan> waits = [];
        var queue = Queue(requests, waits, 200, 429, 429, 429, 200);
        queue.Enqueue(["4000000201", "4000000202"]);
        await queue.WhenIdle();

        Assert.Equal(2, Jobs());
        // 4000000201 lido; 4000000202 recebeu três 429 seguidos antes de passar: esperas de 60, 120 e 240 s, sempre a mesma vaga
        Assert.Equal(["4000000201", "4000000202", "4000000202", "4000000202", "4000000202"], requests.Select(r => r.Id));
        Assert.Equal([60, 120, 240], waits.Where(w => w.TotalSeconds >= 60).Select(w => (int)w.TotalSeconds));
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public async Task Repeated_429s_never_empty_the_queue_and_the_wait_stops_growing_at_fifteen_minutes()
    {
        List<(string Id, int Status)> requests = [];
        List<TimeSpan> waits = [];
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new LinkedInQueue(_db.Db, null, _file,
            fetch: (url, _) => { requests.Add((url[(url.LastIndexOf('/') + 1)..], 429)); return Task.FromResult((429, "")); },
            delay: (span, ct) =>
            {
                lock (waits) { waits.Add(span); if (waits.Count < 8) return Task.CompletedTask; }
                enough.TrySetResult();
                return Task.Delay(Timeout.Infinite, ct); // a partir daqui o relógio "para": o teste já viu o que queria
            },
            pace: () => TimeSpan.FromSeconds(5));
        _queues.Add(queue);
        queue.Enqueue(["4000000301", "4000000302"]);
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(20)); // espera a condição, não um tempo fixo: a máquina pode estar ocupada
        Assert.Equal([60, 120, 240, 480, 900, 900, 900], waits.Take(7).Select(w => (int)w.TotalSeconds));
        Assert.Equal(2, queue.Pending);
        Assert.Equal(0, Jobs());
        Assert.Contains("4000000301", File.ReadAllText(_file)); // continua guardada para a próxima abertura do app
    }

    [Fact]
    public async Task The_queue_survives_closing_the_app_and_skips_what_is_known_or_already_queued()
    {
        {
            var hit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = new LinkedInQueue(_db.Db, null, _file, fetch: (_, _) => { hit.TrySetResult(); return Task.FromResult((429, "")); }, delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
            _queues.Add(blocked);
            blocked.Enqueue(["4000000401", "4000000402"]);
            await hit.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, blocked.Pending);
            blocked.Dispose();
        }
        Assert.True(LinkedInQueue.HasSavedWork(_file));

        List<(string Id, int Status)> requests = [];
        var again = Queue(requests, [], 200); // outra "abertura" do app, com a internet de volta
        Assert.Equal(2, again.Pending);
        again.Resume();
        await again.WhenIdle();
        Assert.Equal(2, Jobs());

        var second = again.Enqueue(["4000000401", "4000000403", "4000000403", "4000000403"]);
        Assert.Equal((1, 1), (second["queued"]!.GetValue<int>(), second["known"]!.GetValue<int>())); // 401 já está no banco; 403 entra uma vez
        await again.WhenIdle();
        Assert.Equal(3, Jobs());
    }

    [Fact]
    public async Task Closed_missing_and_unreadable_pages_leave_the_queue_without_blocking_it()
    {
        List<(string Id, int Status)> requests = [];
        var queue = Queue(requests, [], 404, 500, 200);
        queue.Enqueue(["4000000501", "4000000502", "4000000503"]);
        await queue.WhenIdle();
        Assert.Equal(0, queue.Pending);
        Assert.Equal(1, Jobs());
        var status = queue.Status();
        Assert.Equal((1, 1, 1), (status["dropped"]!.GetValue<int>(), status["failed"]!.GetValue<int>(), status["inserted"]!.GetValue<int>()));
    }

    [Fact]
    public async Task The_history_says_what_happened_to_each_job_newest_first()
    {
        var queue = Queue([], [], 404, 500, 200, 200); // o 4º é a mesma vaga do 3º em conteúdo, com outra id
        queue.Enqueue(["4000000701", "4000000702", "4000000703", "4000000704"]);
        await queue.WhenIdle();

        var now = queue.Snapshot();
        Assert.Equal((false, LinkedInWait.None, 0), (now.Running, now.Wait, now.WaitingSeconds));
        Assert.Empty(now.Pending);
        Assert.Empty(now.CurrentBatch);
        Assert.Equal(["4000000704", "4000000703", "4000000702", "4000000701"], now.History.Select(h => h.Outcome.Id));
        var by = now.History.ToDictionary(h => h.Outcome.Id, h => h.Outcome);
        Assert.Equal((ImportResult.Dropped, "a vaga não existe mais (HTTP 404)"), (by["4000000701"].Kind, by["4000000701"].Detail));
        Assert.Equal((ImportResult.Failed, "o LinkedIn respondeu HTTP 500"), (by["4000000702"].Kind, by["4000000702"].Detail));
        Assert.Equal((ImportResult.Inserted, "Dev Pleno"), (by["4000000703"].Kind, by["4000000703"].Title));
        Assert.Equal(_db.Scalar<long>("SELECT id FROM jobs WHERE source_id='4000000703'"), by["4000000703"].JobId);
        Assert.Equal(now.Inserted + now.Duplicates + now.Dropped + now.Failed, now.History.Count);
    }

    /// <summary>Fila cujo LinkedIn responde 429 até <paramref name="open"/> virar verdadeiro e cujas esperas longas nunca acabam sozinhas.</summary>
    private LinkedInQueue Blocked(Func<bool> open, TaskCompletionSource waiting)
    {
        var queue = new LinkedInQueue(_db.Db, null, _file,
            fetch: (_, _) => Task.FromResult(open() ? (200, Guest) : (429, "")),
            delay: (span, ct) =>
            {
                if (span.TotalSeconds < 60) return Task.CompletedTask;
                waiting.TrySetResult();
                return Task.Delay(Timeout.Infinite, ct);
            },
            pace: () => TimeSpan.FromSeconds(5));
        _queues.Add(queue);
        return queue;
    }

    /// <summary>A espera é anunciada um instante depois de o relógio falso ser chamado, então o teste observa a foto até ela mostrar o esperado.</summary>
    private static async Task<LinkedInQueueSnapshot> Until(LinkedInQueue queue, Func<LinkedInQueueSnapshot, bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var now = queue.Snapshot();
            if (ready(now)) return now;
            Assert.True(DateTime.UtcNow < deadline, "a fila não chegou ao estado esperado");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task During_a_429_the_snapshot_explains_the_wait_and_it_can_be_skipped()
    {
        var open = false;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = Blocked(() => Volatile.Read(ref open), waiting);
        queue.Enqueue(["4000000801", "4000000802"]);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var now = await Until(queue, s => s.Wait == LinkedInWait.RateLimit);
        Assert.Equal(["4000000801", "4000000802"], now.Pending);
        Assert.Empty(now.CurrentBatch); // esperando: nada está sendo lido
        Assert.InRange(now.WaitingSeconds, 1, 60);

        Volatile.Write(ref open, true);
        Assert.True(queue.SkipWait());
        await Until(queue, s => !s.Running);
        Assert.Equal(2, Jobs());
        Assert.False(queue.SkipWait()); // não há mais espera
    }

    [Fact]
    public async Task Clearing_during_a_wait_empties_the_saved_queue_and_stops_the_worker()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = Blocked(() => false, waiting);
        queue.Enqueue(["4000000901", "4000000902"]);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Until(queue, s => s.Wait == LinkedInWait.RateLimit);

        Assert.Equal(2, queue.Clearable);
        Assert.Equal(2, queue.ClearPending());
        await Until(queue, s => !s.Running);
        Assert.Equal(0, queue.Pending);
        Assert.False(LinkedInQueue.HasSavedWork(_file));
        Assert.Equal(0, queue.ClearPending());
    }

    [Fact]
    public async Task Clearing_while_a_batch_is_being_read_keeps_that_batch_and_what_arrives_later()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new LinkedInQueue(_db.Db, null, _file,
            fetch: async (_, _) => { reading.TrySetResult(); await release.Task; return (404, ""); },
            delay: (_, _) => Task.CompletedTask, pace: () => TimeSpan.Zero);
        _queues.Add(queue);
        string[] ids = ["4000001001", "4000001002", "4000001003", "4000001004", "4000001005"];
        queue.Enqueue(ids);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ids[..LinkedInQueue.BatchSize], queue.Snapshot().CurrentBatch);
        Assert.Equal(ids.Length - LinkedInQueue.BatchSize, queue.ClearPending()); // só o que não começou
        queue.Enqueue(["4000001099"]); // chega no meio do lote: não pode sumir junto com ele
        release.SetResult();
        await queue.WhenIdle();

        Assert.Equal(["4000001001", "4000001002", "4000001003", "4000001099"], queue.Snapshot().History.Select(h => h.Outcome.Id).Reverse());
        Assert.Equal(0, queue.Pending);
    }

    [Fact]
    public async Task Every_change_is_announced_on_the_bus()
    {
        var queue = Queue([], [], 200);
        queue.Enqueue(["4000001101"]);
        await queue.WhenIdle();
        Assert.Contains(_db.Changes, c => c is LinkedInQueueChanged);
    }
}

public class LinkedInQueueRouteTests
{
    [Fact]
    public async Task The_extension_route_queues_ids_validates_them_and_reports_progress()
    {
        using var api = ApiHarness.Start();
        var token = new Odradek.Core.Actions.ExtensionTokenActions(api.Temp.Db).Issue();
        var (status, json) = await api.Json("POST", "/api/linkedin-import", token, """{"enqueue":["4000000601","4000000602","4000000601"]}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((2, 0, 0), (json!["queued"]!.GetValue<int>(), json["alreadyQueued"]!.GetValue<int>(), json["known"]!.GetValue<int>()));

        var (_, again) = await api.Json("POST", "/api/linkedin-import", token, """{"enqueue":["4000000602","4000000603"]}""");
        Assert.Equal((1, 1), (again!["queued"]!.GetValue<int>(), again["alreadyQueued"]!.GetValue<int>()));

        var (statusCode, state) = await api.Json("POST", "/api/linkedin-import", token, """{"status":true}""");
        Assert.Equal(HttpStatusCode.OK, statusCode);
        Assert.True(state!["pending"]!.GetValue<int>() >= 1);

        foreach (var bad in new[] { """{"enqueue":[]}""", """{"enqueue":["../x"]}""", "{\"enqueue\":[" + string.Join(',', Enumerable.Range(0, 501).Select(i => $"\"{4000300000 + i}\"")) + "]}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Json("POST", "/api/linkedin-import", token, bad)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/linkedin-import", token: null, body: """{"enqueue":["4000000601"]}""")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/linkedin-import", token: ApiHarness.QpidToken, body: """{"status":true}""")).Status);
    }
}
