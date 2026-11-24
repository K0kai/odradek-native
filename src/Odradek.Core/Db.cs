using Microsoft.Data.Sqlite;

namespace Odradek.Core;

/// <summary>Conexões com o <c>jobs.db</c>: WAL, <c>busy_timeout</c> de 60 s e <c>synchronous=NORMAL</c>, como em <c>data/dbutil.py</c>.</summary>
public static class Db
{
    public static SqliteConnection Open(string? path = null, bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path ?? Paths.DbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = readOnly
            ? "PRAGMA busy_timeout=60000;"
            : "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=60000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
