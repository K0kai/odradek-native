using Dapper;
using Odradek.Core.Data;
using Odradek.Core.Queue;

namespace Odradek.Core.Actions;

/// <summary>
/// Operações da fila de IA no banco (<c>jobs/job_queue.py</c>: enqueue, cancel, retry, clear). Os workers que executam os itens
/// chegam na etapa 4; aqui só o que o painel pede. Cada mudança é registrada em <c>event_logs</c> com source "queue", o que
/// também avisa as telas (<see cref="QueueChanged"/>).
/// </summary>
/// <summary>(workers, tentativas máximas, validade em horas) das configurações, com os mesmos limites e padrões do Python.</summary>
public static class QueueLimits
{
    public static (int Workers, int MaxAttempts, int TtlHours) Read(Database db)
    {
        var cfg = new SettingsStore(db).All();
        int Value(string key, int fallback, int lo, int hi)
        {
            var raw = cfg.TryGetValue(key, out var v) ? v : fallback.ToString();
            if (string.IsNullOrEmpty(raw)) return fallback;
            return int.TryParse(raw.Trim(), out var n) ? Math.Clamp(n, lo, hi) : fallback;
        }
        return (Value("queue_max_workers", 3, 1, 8), Value("queue_max_attempts", 40, 1, 200), Value("queue_ttl_hours", 24, 1, 168));
    }
}

public sealed class QueueActions(Database db)
{
    public const string KindApply = "job_apply";
    public const string KindResume = "resume_analysis";

    private readonly EventLogStore _log = new(db);

    private static string Iso(DateTime utc) => Clock.ToIso(utc);

    private (int Workers, int MaxAttempts, int TtlHours) Limits() => QueueLimits.Read(db);

    /// <summary>Cria um item pendente; com <paramref name="dedupeKey"/> reaproveita um item ativo do mesmo tipo e chave.</summary>
    public long Enqueue(string kind, IReadOnlyList<KeyValuePair<string, object?>> payload, string? dedupeKey = null)
    {
        if (kind is not (KindApply or KindResume or "worth_reverify")) throw new ActionException($"Tipo de job desconhecido: {kind}");
        var (_, maxAttempts, ttlHours) = Limits();
        var now = DateTime.UtcNow;
        var fields = payload.ToList();
        if (!string.IsNullOrEmpty(dedupeKey))
        {
            fields.Add(new("_dedupe", dedupeKey));
            using var check = db.Open();
            var existing = check.ExecuteScalar<long?>(
                "SELECT id FROM queue_jobs WHERE kind=@kind AND status IN ('pending','running','retry_wait') AND payload LIKE @like ORDER BY id DESC LIMIT 1",
                new { kind, like = $"%\"{dedupeKey}\"%" });
            if (existing is { } id)
            {
                _log.Add("info", "queue", $"Job #{id} ({kind}) já na fila; reutilizando.");
                QueueRuntime.Wake(db);
                return id;
            }
        }
        long created;
        using (var connection = db.Open())
            created = connection.ExecuteScalar<long>(
                @"INSERT INTO queue_jobs(kind,payload,status,attempts,max_attempts,next_run_at,expires_at,last_error,result,created_at,updated_at)
                  VALUES(@kind,@payload,'pending',0,@maxAttempts,@now,@expires,'','',@now,@now) RETURNING id",
                new { kind, payload = PyJson.Dumps(fields), maxAttempts, now = Iso(now), expires = Iso(now.AddHours(ttlHours)) });
        _log.Add("info", "queue", $"Enfileirado #{created} ({kind}).");
        QueueRuntime.Wake(db);
        return created;
    }

    public bool Cancel(long id)
    {
        int rows;
        using (var connection = db.Open())
            rows = connection.Execute(
                @"UPDATE queue_jobs SET status='cancelled', updated_at=@now, last_error=COALESCE(NULLIF(last_error,''),'Cancelado manualmente')
                  WHERE id=@id AND status IN ('pending','retry_wait','running')", new { now = Iso(DateTime.UtcNow), id });
        if (rows > 0)
        {
            QueueRuntime.Interrupt(db, id); // se está rodando neste processo, as chamadas em andamento são abortadas
            _log.Add("warning", "queue", $"Job #{id} cancelado.");
        }
        return rows > 0;
    }

    public bool RetryNow(long id)
    {
        if (QueueRuntime.IsRunning(db, id)) return false; // executando agora neste processo: cancele primeiro
        var (_, maxAttempts, ttlHours) = Limits();
        var now = DateTime.UtcNow;
        int rows;
        using (var connection = db.Open())
            rows = connection.Execute(
                @"UPDATE queue_jobs SET status='pending', next_run_at=@now, expires_at=@expires, updated_at=@now, last_error='', attempts=0, max_attempts=@maxAttempts, result=''
                  WHERE id=@id AND kind!='linkedin_apply' AND status IN ('retry_wait','failed','cancelled','running')",
                new { now = Iso(now), expires = Iso(now.AddHours(ttlHours)), maxAttempts, id });
        if (rows > 0)
        {
            _log.Add("info", "queue", $"Job #{id} reenfileirado para execução imediata.");
            QueueRuntime.Wake(db);
        }
        return rows > 0;
    }

    public int RetryAll()
    {
        var (_, maxAttempts, ttlHours) = Limits();
        var now = DateTime.UtcNow;
        int rows;
        using (var connection = db.Open())
        {
            var live = QueueRuntime.RunningIds(db);
            var ids = connection.Query<long>("SELECT id FROM queue_jobs WHERE kind!='linkedin_apply' AND status IN ('failed','cancelled','retry_wait','running')")
                .Where(i => !live.Contains(i)).ToList();
            if (ids.Count == 0) return 0;
            rows = connection.Execute(
                @"UPDATE queue_jobs SET status='pending', next_run_at=@now, expires_at=@expires, updated_at=@now, last_error='', attempts=0, max_attempts=@maxAttempts, result=''
                  WHERE id IN @ids", new { now = Iso(now), expires = Iso(now.AddHours(ttlHours)), maxAttempts, ids });
        }
        if (rows > 0)
        {
            _log.Add("info", "queue", $"{rows} job(s) reenfileirado(s) em lote.");
            QueueRuntime.Wake(db);
        }
        return rows;
    }

    public int ClearTerminal()
    {
        int rows;
        using (var connection = db.Open())
            rows = connection.Execute("DELETE FROM queue_jobs WHERE status IN ('succeeded','failed','cancelled')");
        _log.Add("info", "queue", $"Removidos {rows} job(s) concluídos/falhos/cancelados.");
        return rows;
    }

    /// <summary>"Triar vagas novas": enfileira as vagas 'new' (países com viés e mais recentes primeiro) e as move para 'review'.</summary>
    public string EvaluateNew(int? limit = null)
    {
        var settings = new SettingsStore(db).All();
        List<(long Id, string? Location, string? PostedAt)> rows;
        using (var connection = db.Open())
            rows = connection.Query<(long, string?, string?)>("SELECT id, location, posted_at FROM jobs WHERE status='new'").ToList();
        if (rows.Count == 0) return "nenhuma vaga nova para enfileirar";
        var preferred = Geo.PreferredCountries(settings.GetValueOrDefault("linkedin_preferred_countries"));
        var ordered = rows.Select(r => (Row: r, Key: Geo.RecencyKey(r.Location, r.PostedAt, preferred)))
            .OrderBy(x => x.Key.Group).ThenBy(x => x.Key.Age).ThenByDescending(x => x.Row.Id).Select(x => x.Row).ToList();
        var remaining = limit is null ? 0 : Math.Max(ordered.Count - limit.Value, 0);
        if (limit is not null) ordered = ordered.Take(limit.Value).ToList();
        foreach (var row in ordered)
        {
            Enqueue(KindApply, [new("job_id", row.Id)], $"apply:{row.Id}");
            using var connection = db.Open();
            connection.Execute("UPDATE jobs SET status='review', notes=@notes WHERE id=@id AND status='new'",
                new { notes = "Na fila de triagem (processada pelos workers da fila).", id = row.Id });
        }
        db.Bus.Publish(new JobsChanged([]));
        var workers = settings.TryGetValue("queue_max_workers", out var w) ? w : "3";
        var note = $"{ordered.Count} vaga(s) enfileirada(s) para triagem (até {workers} worker(s) em paralelo)";
        if (remaining > 0) note += $"; limite atingido, {remaining} vaga(s) nova(s) ficaram para a próxima execução";
        return note;
    }
}
