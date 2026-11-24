using Dapper;
using Microsoft.Data.Sqlite;

namespace Odradek.Core.Data;

/// <summary>
/// O <c>jobs.db</c> compartilhado com o app Python. Cada chamada abre uma conexão do pool (barata no SQLite local);
/// WAL, <c>busy_timeout</c> de 60 s e <c>synchronous=NORMAL</c> como em <c>data/dbutil.py</c>.
/// </summary>
public sealed class Database
{
    static Database()
    {
        // Dapper: coluna match_score -> propriedade MatchScore
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Database(string? path = null, bool readOnly = false, ChangeBus? bus = null)
    {
        Path = path ?? Paths.DbPath;
        ReadOnly = readOnly;
        Bus = bus ?? ChangeBus.Shared;
    }

    public string Path { get; }
    public bool ReadOnly { get; }
    public ChangeBus Bus { get; }

    public SqliteConnection Open() => Db.Open(Path, ReadOnly);

    /// <summary>Cria o arquivo e aplica o esquema e as migrações (idempotente, igual ao <c>initialize()</c> do Python).</summary>
    public void Initialize()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Schema.Apply(connection, transaction);
        transaction.Commit();
    }
}
