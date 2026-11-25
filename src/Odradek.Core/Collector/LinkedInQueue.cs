using System.Text.Json;
using System.Text.Json.Nodes;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.Core.Collector;

/// <summary>Por que a fila está parada esperando: pausa normal entre lotes, o LinkedIn pediu calma (429) ou rede/banco indisponíveis.</summary>
public enum LinkedInWait { None, Pace, RateLimit, Network }

/// <summary>Uma vaga que já saiu da fila nesta execução do app.</summary>
public sealed record LinkedInProcessed(ImportOutcome Outcome, DateTime ProcessedAt);

/// <summary>Foto da fila para a tela. <see cref="Pending"/> está na ordem de leitura e inclui o lote em leitura (<see cref="CurrentBatch"/>).</summary>
public sealed record LinkedInQueueSnapshot(IReadOnlyList<string> Pending, IReadOnlyList<string> CurrentBatch, IReadOnlyList<LinkedInProcessed> History,
    bool Running, int WaitingSeconds, LinkedInWait Wait, int Inserted, int Duplicates, int Dropped, int Failed);

/// <summary>
/// Fila em segundo plano das vagas do LinkedIn que a extensão encontrou: ela só manda as ids e termina na hora; o Odradek lê a página pública de cada
/// uma devagar (3 por vez, pausa de alguns segundos) e, se o LinkedIn pedir calma (429), espera cada vez mais e tenta de novo, sem desistir nem travar
/// ninguém. A fila fica num arquivo da pasta de dados, então sobrevive a fechar o app (o banco não é alterado: o esquema é do app Python).
/// </summary>
public sealed class LinkedInQueue : IDisposable
{
    public const int MaxPerCall = 500;
    public const int BatchSize = 3;
    public static readonly int[] BackoffSeconds = [60, 120, 240, 480, 900];
    public const int MaxHistory = 200;

    private readonly Database _db;
    private readonly Func<HttpClient>? _http;
    private readonly string _file;
    private readonly Func<string, CancellationToken, Task<(int Status, string Body)>>? _fetch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan> _pace;
    private readonly object _gate = new();
    private readonly List<string> _ids = [];
    private readonly List<string> _batch = []; // lote em leitura (continua em _ids até cada vaga ter um desfecho)
    private readonly List<LinkedInProcessed> _history = []; // mais recentes primeiro; só na memória
    private CancellationTokenSource? _wake; // interrompe só a espera atual, sem parar a fila
    private LinkedInWait _wait;
    private readonly CancellationTokenSource _stop = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private int _inserted, _duplicates, _dropped, _failed, _strikes;
    private DateTime? _waitingUntil;

    public static string DefaultFile => Path.Combine(Paths.DataDir, "linkedin-queue.json");

    public LinkedInQueue(Database db, Func<HttpClient>? http, string? file = null, Func<string, CancellationToken, Task<(int Status, string Body)>>? fetch = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<TimeSpan>? pace = null)
    {
        _db = db;
        _http = http;
        _file = file ?? DefaultFile;
        _fetch = fetch;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _pace = pace ?? (() => TimeSpan.FromMilliseconds(Random.Shared.Next(5000, 8001)));
        _ids.AddRange(Load(_file));
    }

    /// <summary>Há ids guardadas de uma execução anterior? (Sem montar nada: serve para decidir se vale retomar ao abrir o app.)</summary>
    public static bool HasSavedWork(string? file = null) => Load(file ?? DefaultFile).Count > 0;

    private static List<string> Load(string file)
    {
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root || root["ids"] is not JsonArray ids) return [];
            return ids.Select(node => Text.PyStr(node).Trim()).Where(LinkedInImport.IsValidId).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, new JsonObject { ["ids"] = new JsonArray(_ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()) }.ToJsonString());
            File.Move(temp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a fila continua na memória; só não sobrevive ao fechar */ }
    }

    /// <summary>Entra na fila o que o app ainda não tem e ainda não está na fila; liga o trabalho em segundo plano.</summary>
    public JsonObject Enqueue(IEnumerable<string> ids)
    {
        var known = LinkedInImport.KnownIds(_db);
        int added = 0, queued = 0, haveIt = 0;
        lock (_gate)
        {
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                if (known.Contains(id)) haveIt++;
                else if (_ids.Contains(id)) queued++;
                else { _ids.Add(id); added++; }
            }
            if (added > 0) Save();
        }
        Resume();
        if (added > 0) Changed();
        return new JsonObject { ["ok"] = true, ["queued"] = added, ["alreadyQueued"] = queued, ["known"] = haveIt, ["pending"] = Pending };
    }

    public int Pending { get { lock (_gate) return _ids.Count; } }

    /// <summary>Quantas vagas <see cref="ClearPending"/> tiraria agora (o lote em leitura fica).</summary>
    public int Clearable { get { lock (_gate) return _ids.Count - _batch.Count; } }

    public LinkedInQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            var wait = _waitingUntil is { } until ? Math.Max(0, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds)) : 0;
            return new LinkedInQueueSnapshot([.. _ids], [.. _batch], [.. _history], _running, wait, _wait, _inserted, _duplicates, _dropped, _failed);
        }
    }

    public JsonObject Status()
    {
        var now = Snapshot();
        return new JsonObject
        {
            ["ok"] = true, ["pending"] = now.Pending.Count, ["running"] = now.Running, ["waitingSeconds"] = now.WaitingSeconds,
            ["inserted"] = now.Inserted, ["duplicates"] = now.Duplicates, ["dropped"] = now.Dropped, ["failed"] = now.Failed,
        };
    }

    /// <summary>Tira da fila o que ainda não começou a ser lido; o lote em leitura termina normalmente. Devolve quantas saíram.</summary>
    public int ClearPending()
    {
        int removed;
        lock (_gate)
        {
            removed = _ids.RemoveAll(id => !_batch.Contains(id));
            if (removed == 0) return 0;
            Save();
            if (_ids.Count == 0) { _strikes = 0; _ = _wake?.CancelAsync(); } // não há mais o que esperar
        }
        Changed();
        return removed;
    }

    /// <summary>
    /// Interrompe a espera depois de um 429 ou de uma falha de rede e tenta agora, recomeçando a contagem das esperas.
    /// A pausa normal entre lotes não é pulada. Devolve <c>false</c> se não havia o que interromper.
    /// </summary>
    public bool SkipWait()
    {
        lock (_gate)
        {
            if (_wake is null || _wait is not (LinkedInWait.RateLimit or LinkedInWait.Network)) return false;
            _strikes = 0;
            _ = _wake.CancelAsync(); // assíncrono: o trabalho não continua na thread de quem chamou (a da tela)
            return true;
        }
    }

    private void Changed() => _db.Bus.Publish(new LinkedInQueueChanged());

    /// <summary>Uma vaga ganhou desfecho: sai da fila (pela id, não pela posição: a fila pode ter sido esvaziada no meio do lote).</summary>
    private void Finish(ImportOutcome outcome)
    {
        lock (_gate)
        {
            _ids.Remove(outcome.Id);
            _batch.Remove(outcome.Id);
            switch (outcome.Kind)
            {
                case ImportResult.Inserted: _inserted++; break;
                case ImportResult.Duplicate: _duplicates++; break;
                case ImportResult.Dropped: _dropped++; break;
                default: _failed++; break;
            }
            _history.Insert(0, new LinkedInProcessed(outcome, DateTime.UtcNow));
            if (_history.Count > MaxHistory) _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            Save();
        }
        Changed();
    }

    /// <summary>Espera o trabalho em segundo plano acabar (testes).</summary>
    public Task WhenIdle() { lock (_gate) return _worker; }

    /// <summary>Liga o trabalho se houver ids e ele ainda não estiver rodando.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_running || _ids.Count == 0 || _stop.IsCancellationRequested) return;
            _running = true;
            _worker = Task.Run(WorkAsync);
        }
    }

    private async Task WorkAsync()
    {
        var ct = _stop.Token;
        var released = false; // já marcou "parado": depois disso outro trabalho pode ter começado, e o finally não pode mexer no estado dele
        try
        {
            while (!ct.IsCancellationRequested)
            {
                List<string> batch = [];
                lock (_gate)
                {
                    if (_ids.Count == 0) { _running = false; released = true; }
                    else
                    {
                        batch = _ids.Take(BatchSize).ToList();
                        _batch.AddRange(batch);
                    }
                }
                if (released) return;
                Changed();
                ImportSummary summary;
                try
                {
                    var cards = batch.Select(id => new ImportCard(id, "", "", "")).ToList();
                    var fetch = _fetch ?? (_http?.Invoke() is { } client ? new WorthReverify(_db, client).FetchUrlTextAsync : null);
                    if (fetch is null)
                    {
                        EndBatch();
                        await WaitAsync(TimeSpan.FromSeconds(BackoffSeconds[^1]), LinkedInWait.Network, ct).ConfigureAwait(false);
                        continue;
                    }
                    summary = await LinkedInImport.RunAsync(_db, cards, fetch, pause: token => _delay(_pace(), token), ct: ct, report: Finish).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                {
                    EndBatch();
                    await Backoff(LinkedInWait.Network, ct).ConfigureAwait(false); // rede fora do ar ou banco ocupado: tenta de novo depois, sem perder a fila
                    continue;
                }
                EndBatch(); // o que ficou "pending" (a primeira delas levou o 429) continua na fila
                if (Pending == 0) _strikes = 0;
                else if (summary.Pending > 0) await Backoff(LinkedInWait.RateLimit, ct).ConfigureAwait(false);
                else
                {
                    _strikes = 0;
                    await WaitAsync(_pace(), LinkedInWait.Pace, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* app fechando */ }
        finally
        {
            if (!released) lock (_gate) { _running = false; _waitingUntil = null; _batch.Clear(); }
            Changed();
        }
    }

    private void EndBatch()
    {
        lock (_gate) _batch.Clear();
        Changed();
    }

    private async Task Backoff(LinkedInWait reason, CancellationToken ct)
    {
        int seconds;
        lock (_gate)
        {
            seconds = BackoffSeconds[Math.Min(_strikes, BackoffSeconds.Length - 1)];
            _strikes++;
        }
        await WaitAsync(TimeSpan.FromSeconds(seconds), reason, ct).ConfigureAwait(false);
    }

    private async Task WaitAsync(TimeSpan span, LinkedInWait reason, CancellationToken ct)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) { _waitingUntil = DateTime.UtcNow + span; _wait = reason; _wake = wake; }
        Changed();
        try { await _delay(span, wake.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* "retomar agora" ou fila esvaziada */ }
        finally
        {
            lock (_gate) { _waitingUntil = null; _wait = LinkedInWait.None; _wake = null; }
            Changed();
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { /* já cancelado */ }
        _stop.Dispose();
    }
}
