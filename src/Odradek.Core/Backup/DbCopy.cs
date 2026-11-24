using System.Globalization;
using System.Reflection;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using Odradek.Core.Actions;
using Odradek.Core.Data;

namespace Odradek.Core.Backup;

/// <summary>
/// Copia o banco local (SQLite) para um PostgreSQL de backup (<c>data/dbcopy.py</c>). O destino é substituído por inteiro numa transação:
/// se algo falhar, ele fica como estava. A origem é lida de um instantâneo consistente feito pela API de backup do SQLite, então o app pode
/// continuar gravando enquanto a cópia roda.
/// </summary>
public static class DbCopy
{
    /// <summary>Ordem que respeita as chaves estrangeiras (pais antes dos filhos).</summary>
    public static readonly string[] TableOrder =
    [
        "settings", "jobs", "runs", "ai_decisions", "cover_letters", "resumes", "resume_files", "form_field_rules", "applications",
        "qpid_application_attempts", "qpid_application_results", "form_answers", "ai_answer_cache", "copilot_asks", "queue_jobs", "event_logs",
    ];

    private static readonly string[] RequiredTables =
    [
        "settings", "jobs", "runs", "ai_decisions", "cover_letters", "resumes", "resume_files", "form_field_rules", "applications", "form_answers",
        "ai_answer_cache", "copilot_asks", "queue_jobs", "event_logs", "qpid_application_attempts", "qpid_application_results",
    ];

    /// <summary>Colunas acrescentadas depois da primeira versão (<c>NEW_COLUMNS</c> do repositório).</summary>
    private static readonly (string Table, string Column, string Definition)[] NewColumns =
    [
        ("resumes", "profile_json", "TEXT NOT NULL DEFAULT ''"), ("resumes", "profile_version", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "assessment_json", "TEXT NOT NULL DEFAULT ''"), ("ai_decisions", "scoring_version", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "provider", "TEXT NOT NULL DEFAULT ''"), ("ai_decisions", "model", "TEXT NOT NULL DEFAULT ''"),
        ("jobs", "search_filter_id", "TEXT NOT NULL DEFAULT ''"), ("jobs", "search_remote", "TEXT NOT NULL DEFAULT ''"), ("jobs", "reviewed_at", "TEXT NOT NULL DEFAULT ''"),
    ];

    /// <summary>As instruções do <c>schema_postgres.sql</c> (embutido), uma a uma; comentários de linha inteira são ignorados.</summary>
    public static List<string> SchemaStatements()
    {
        using var stream = typeof(DbCopy).Assembly.GetManifestResourceStream("schema_postgres.sql") ?? throw new InvalidOperationException("schema_postgres.sql não está embutido.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal));
        return (string.Join('\n', lines) + "\n").Split(";\n").Select(s => s.Trim()).Where(s => s.TrimEnd(';').Length > 0).Select(s => s.TrimEnd(';')).ToList();
    }

    // ------------------------------------------------------------------ instantâneo do SQLite

    /// <summary>Cria um arquivo coerente do banco ativo em <paramref name="folder"/>. PDFs só guardados no disco (bancos antigos) entram nele.</summary>
    public static string CreateSnapshot(Database source, string folder)
    {
        if (!File.Exists(source.Path)) throw new BackupException("Banco local não encontrado.");
        var path = System.IO.Path.Combine(folder, "snapshot.db");
        using (var from = source.Open())
        using (var to = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            to.Open();
            from.BackupDatabase(to);
        }
        using (var snapshot = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            snapshot.Open();
            var tables = snapshot.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet();
            if (tables.Contains("resumes") && tables.Contains("resume_files"))
            {
                foreach (var language in snapshot.Query<string>("SELECT r.language FROM resumes r LEFT JOIN resume_files f ON f.resume_id = r.id WHERE f.resume_id IS NULL").ToList())
                {
                    var pdf = ResumeActions.ReadPdf(snapshot, language);
                    if (pdf is null) continue;
                    snapshot.Execute("INSERT INTO resume_files(resume_id, content) SELECT id, @pdf FROM resumes WHERE language=@language", new { pdf, language });
                }
            }
        }
        return path;
    }

    // ------------------------------------------------------------------ cópia para o Postgres

    /// <summary>Converte o valor do SQLite para o tipo da coluna de destino (o SQLite aceita qualquer tipo em qualquer coluna; o Postgres não).</summary>
    internal static object ToPostgres(object? value, string pgType)
    {
        if (value is null or DBNull) return DBNull.Value;
        switch (pgType)
        {
            case "text" or "character varying" or "character":
                return value switch
                {
                    string s => s.Replace("\0", "", StringComparison.Ordinal), // o Postgres não guarda NUL em texto
                    byte[] bytes => Encoding.UTF8.GetString(bytes).Replace("\0", "", StringComparison.Ordinal),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? "",
                };
            case "bigint" or "integer" or "smallint":
                return value switch
                {
                    long n => n,
                    double d => (long)d,
                    string s when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => value,
                };
            case "double precision" or "real":
                return value switch { long n => (double)n, string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d, _ => value };
            case "bytea":
                return value is string text ? Encoding.UTF8.GetBytes(text) : value;
            default:
                return value;
        }
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private const int BatchSize = 100;

    /// <summary>Substitui o conteúdo do Postgres de <paramref name="target"/> pelo do instantâneo, numa transação. Devolve {tabela: linhas}.</summary>
    public static async Task<Dictionary<string, int>> CopyToPostgresAsync(string snapshotPath, NpgsqlConnectionStringBuilder target, Action<string> log, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>();
        using var source = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly;Pooling=False");
        source.Open();
        var sourceTables = source.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet();
        var tables = TableOrder.Where(sourceTables.Contains).ToList();
        foreach (var missing in TableOrder.Where(t => !sourceTables.Contains(t))) log($"Origem sem a tabela {missing} (ignorada).");

        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false); // uma transação: tudo ou nada

        async Task Run(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        async Task<List<(string A, string B, string C)>> Rows3(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<(string, string, string)>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return rows;
        }

        var present = (await Rows3("SELECT table_name, '', '' FROM information_schema.tables WHERE table_schema = current_schema()").ConfigureAwait(false)).Select(r => r.A).ToHashSet();
        if (RequiredTables.Except(present).Any())
        {
            foreach (var statement in SchemaStatements()) await Run(statement).ConfigureAwait(false);
            present = (await Rows3("SELECT table_name, '', '' FROM information_schema.tables WHERE table_schema = current_schema()").ConfigureAwait(false)).Select(r => r.A).ToHashSet();
        }
        foreach (var (table, column, definition) in NewColumns) await Run($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} {definition}").ConfigureAwait(false);

        var copyTables = tables.Where(present.Contains).ToList();
        if (copyTables.Count > 0) await Run($"TRUNCATE TABLE {string.Join(", ", copyTables.Select(Quote))} RESTART IDENTITY CASCADE").ConfigureAwait(false);
        var targetColumns = (await Rows3("SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = current_schema()").ConfigureAwait(false))
            .GroupBy(r => r.A).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.B, r => r.C));

        foreach (var table in copyTables)
        {
            var types = targetColumns.GetValueOrDefault(table) ?? [];
            var columns = source.Query<(long Cid, string Name)>($"SELECT cid AS Cid, name AS Name FROM pragma_table_info('{table}')").Select(c => c.Name).Where(types.ContainsKey).ToList();
            var rows = 0;
            if (columns.Count > 0)
            {
                var insert = $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select((_, i) => "$" + (i + 1)))})";
                using var select = source.CreateCommand();
                select.CommandText = $"SELECT {string.Join(", ", columns.Select(Quote))} FROM {Quote(table)}";
                using var reader = select.ExecuteReader();
                var batch = new NpgsqlBatch(connection, transaction);
                while (reader.Read())
                {
                    var command = new NpgsqlBatchCommand(insert);
                    for (var i = 0; i < columns.Count; i++)
                        command.Parameters.Add(new NpgsqlParameter { Value = ToPostgres(reader.IsDBNull(i) ? null : reader.GetValue(i), types[columns[i]]) });
                    batch.BatchCommands.Add(command);
                    rows++;
                    if (batch.BatchCommands.Count >= BatchSize) { await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false); batch.Dispose(); batch = new NpgsqlBatch(connection, transaction); }
                }
                if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                batch.Dispose();
                if (rows > 0 && columns.Contains("id"))
                    await Run($"SELECT setval(pg_get_serial_sequence('{Quote(table).Replace("'", "''")}', 'id'), GREATEST(COALESCE(MAX(id), 1), 1), MAX(id) IS NOT NULL) FROM {Quote(table)}").ConfigureAwait(false);
            }
            counts[table] = rows;
            log($"{table}: {rows} linha(s)");
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return counts;
    }

    /// <summary>Instantâneo do banco local → Postgres. O arquivo temporário some ao fim.</summary>
    public static async Task<Dictionary<string, int>> CopyLocalAsync(Database source, string url, Action<string> log, CancellationToken ct = default)
    {
        var target = PostgresUrl.Parse(url);
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "odradek-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var snapshot = await Task.Run(() => CreateSnapshot(source, folder), ct).ConfigureAwait(false);
            return await CopyToPostgresAsync(snapshot, target, log, ct).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
