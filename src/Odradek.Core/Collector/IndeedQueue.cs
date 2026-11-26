using Odradek.Core.Data;

namespace Odradek.Core.Collector;

/// <summary>Em que pé está o envio do Indeed. <see cref="Stalled"/>: estava recebendo e a extensão sumiu (painel ou aba fechados).</summary>
public enum IndeedState { Idle, Receiving, Stalled, Done, Blocked, LeftPage, Unreadable, Failed }

/// <summary>Uma vaga que a extensão avisou que vai ler.</summary>
public sealed record IndeedPending(string Id, string Title, string Company, string Location, string Host);

/// <summary>Uma vaga do Indeed que já teve um desfecho nesta execução do app.</summary>
public sealed record IndeedProcessed(ImportOutcome Outcome, string Host, DateTime ProcessedAt);

/// <summary>Foto da fila para a tela. <see cref="Pending"/> está na ordem de leitura e inclui o lote em leitura (<see cref="CurrentBatch"/>).</summary>
public sealed record IndeedQueueSnapshot(IReadOnlyList<IndeedPending> Pending, IReadOnlyList<IndeedPending> CurrentBatch, IReadOnlyList<IndeedProcessed> History,
    IndeedState State, int Inserted, int Duplicates, int Dropped, int Failed);

/// <summary>
/// O andamento do envio do Indeed, para a tela de Filas. Ao contrário da <see cref="LinkedInQueue"/>, o app não trabalha aqui: quem lê as vagas é a
/// aba do usuário, e a extensão conta o que vai ler e o que aconteceu. Fica só na memória, porque sem a extensão o app não tem como ler o que sobrou.
/// </summary>
public sealed class IndeedQueue(Database db, Func<DateTime>? clock = null)
{
    /// <summary>O mesmo tamanho de lote da extensão (<c>INDEED_BATCH_SIZE</c>).</summary>
    public const int BatchSize = 5;
    public const int MaxHistory = 200;
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(2);

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private readonly List<IndeedPending> _pending = [];
    private readonly List<IndeedProcessed> _history = []; // mais recentes primeiro
    private IndeedState _state;
    private DateTime _lastNews;
    private bool _cancelled;
    private int _inserted, _duplicates, _dropped, _failed;

    public int Pending { get { lock (_gate) return _pending.Count; } }

    /// <summary>O usuário esvaziou a fila na tela: a extensão deve parar de ler.</summary>
    public bool Cancelled { get { lock (_gate) return _cancelled; } }

    public IndeedQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            var state = _state == IndeedState.Receiving && _clock() - _lastNews > StallAfter ? IndeedState.Stalled : _state;
            IReadOnlyList<IndeedPending> batch = state == IndeedState.Receiving ? [.. _pending.Take(BatchSize)] : [];
            return new IndeedQueueSnapshot([.. _pending], batch, [.. _history], state, _inserted, _duplicates, _dropped, _failed);
        }
    }

    /// <summary>Começo de um envio: a lista nova substitui o que tivesse sobrado do anterior.</summary>
    public void Announce(IEnumerable<IndeedPending> jobs)
    {
        lock (_gate)
        {
            _pending.Clear();
            _pending.AddRange(jobs.DistinctBy(job => job.Id));
            _cancelled = false;
            _state = _pending.Count > 0 ? IndeedState.Receiving : IndeedState.Done;
            _lastNews = _clock();
        }
        Changed();
    }

    /// <summary>Uma vaga ganhou desfecho: sai da fila e entra no histórico.</summary>
    public void Report(ImportOutcome outcome, string host)
    {
        lock (_gate)
        {
            _pending.RemoveAll(job => job.Id == outcome.Id);
            switch (outcome.Kind)
            {
                case ImportResult.Inserted: _inserted++; break;
                case ImportResult.Duplicate: _duplicates++; break;
                case ImportResult.Dropped: _dropped++; break;
                default: _failed++; break;
            }
            _history.Insert(0, new IndeedProcessed(outcome, host, _clock()));
            if (_history.Count > MaxHistory) _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
            _lastNews = _clock();
        }
        Changed();
    }

    /// <summary>Uma vaga anunciada que a aba não conseguiu entregar. Ids que não estão na fila são ignoradas.</summary>
    public void Skip(string id, ImportResult kind, string detail)
    {
        IndeedPending? job;
        lock (_gate) job = _pending.FirstOrDefault(p => p.Id == id);
        if (job is not null) Report(new ImportOutcome(id, kind, detail, null, job.Title), job.Host);
    }

    /// <summary>A extensão parou. O que não foi lido continua na lista; depois de "Esvaziar fila" o aviso é ignorado.</summary>
    public void Finish(IndeedState reason)
    {
        lock (_gate)
        {
            if (_cancelled) return;
            _state = reason;
        }
        Changed();
    }

    /// <summary>Tira da lista o que ainda não chegou e pede à extensão que pare. Devolve quantas saíram.</summary>
    public int ClearPending()
    {
        int removed;
        lock (_gate)
        {
            removed = _pending.Count;
            if (removed == 0) return 0;
            _pending.Clear();
            _cancelled = true;
            _state = IndeedState.Idle;
        }
        Changed();
        return removed;
    }

    private void Changed() => db.Bus.Publish(new IndeedQueueChanged());
}
