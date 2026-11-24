using Dapper;

namespace Odradek.Core.Data;

/// <summary>Configurações salvas na tabela <c>settings</c>. SQLite local lê direto (a conexão é barata), sem o cache de 30 s do Postgres.</summary>
public sealed class SettingsStore(Database db)
{
    public Dictionary<string, string> All()
    {
        using var connection = db.Open();
        return connection.Query<(string Key, string Value)>("SELECT key, value FROM settings").ToDictionary(r => r.Key, r => r.Value);
    }

    public string Get(string key, string fallback = "")
    {
        using var connection = db.Open();
        return connection.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@key", new { key }) ?? fallback;
    }

    public void Set(string key, string value) => SetMany(new Dictionary<string, string> { [key] = value });

    /// <summary>Várias chaves numa transação só; avisa as telas depois do commit.</summary>
    public void SetMany(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return;
        using (var connection = db.Open())
        using (var tx = connection.BeginTransaction())
        {
            foreach (var (key, value) in values)
                connection.Execute("INSERT INTO settings(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    new { key, value }, tx);
            tx.Commit();
        }
        db.Bus.Publish(new SettingsChanged(values.Keys.ToArray()));
    }

    /// <summary>Só as chaves conhecidas (<see cref="Defaults.Settings"/>), como o <c>save_settings</c> do Python.</summary>
    public void Save(IReadOnlyDictionary<string, string> data)
    {
        var known = Defaults.Settings.Select(s => s.Key).ToHashSet();
        SetMany(data.Where(kv => known.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));
    }
}
