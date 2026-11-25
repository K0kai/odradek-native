using Dapper;

namespace Odradek.Core.Data;

/// <summary>Log persistente da aba Logs (<c>web/panel_log.py</c>): as 800 linhas mais novas, gravadas na hora no SQLite local.</summary>
public sealed class EventLogStore(Database db)
{
    public const int MaxRows = 800;
    private static readonly HashSet<string> Levels = ["info", "success", "warning", "error", "debug"];
    private readonly object _lock = new();

    public void Add(string level, string source, string message)
    {
        level = (level ?? "info").Trim().ToLowerInvariant();
        if (!Levels.Contains(level)) level = "info";
        source = Text.Take(string.IsNullOrEmpty(source) ? "app" : source, 80);
        message = Text.Take((message ?? "").Trim(), 2000);
        if (message.Length == 0) return;
        var stamp = Clock.NowIso();
        long id;
        try
        {
            lock (_lock)
            {
                using var connection = db.Open();
                id = connection.ExecuteScalar<long>(
                    "INSERT INTO event_logs(created_at,level,source,message) VALUES(@stamp,@level,@source,@message); SELECT last_insert_rowid();",
                    new { stamp, level, source, message });
                // mantém as MaxRows mais novas; sem linhas além disso a subconsulta é NULL e nada é apagado
                connection.Execute("DELETE FROM event_logs WHERE id <= (SELECT id FROM event_logs ORDER BY id DESC LIMIT 1 OFFSET @max)", new { max = MaxRows });
            }
        }
        catch (Exception) { return; } // o log nunca derruba quem o chama
        db.Bus.Publish(new LogAppended(new LogEntry(id, stamp, level, source, message)));
        if (source == "queue") db.Bus.Publish(new QueueChanged(null)); // toda mudança de estado da fila é registrada aqui
    }

    public IReadOnlyList<LogEntry> List(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, MaxRows);
        using var connection = db.Open();
        return connection.Query<LogEntry>("SELECT id AS Id, created_at AS CreatedAt, level AS Level, source AS Source, message AS Message FROM event_logs ORDER BY id DESC LIMIT @limit", new { limit }).ToList();
    }

    public int Clear()
    {
        int removed;
        lock (_lock)
        {
            using var connection = db.Open();
            removed = connection.Execute("DELETE FROM event_logs");
        }
        db.Bus.Publish(new LogsCleared());
        return removed;
    }
}
