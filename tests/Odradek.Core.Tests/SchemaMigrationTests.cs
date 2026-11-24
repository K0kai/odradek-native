using Dapper;
using Microsoft.Data.Sqlite;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>
/// O app novo migra o banco sozinho ao abrir (o Python deixou de ser o dono do esquema). Um banco de uma versão antiga precisa ganhar as
/// colunas novas sem perder dados nem configurações do usuário, e abrir de novo não pode mudar nada.
/// </summary>
public class SchemaMigrationTests
{
    private static string OldDatabase(string dir)
    {
        var path = Path.Combine(dir, "jobs.db");
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        c.Execute("""
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, source_id TEXT, title TEXT NOT NULL, company TEXT NOT NULL DEFAULT '',
              location TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT '', url TEXT NOT NULL, posted_at TEXT, first_seen_at TEXT NOT NULL,
              status TEXT NOT NULL DEFAULT 'new', applied_at TEXT, notes TEXT NOT NULL DEFAULT '', fingerprint TEXT NOT NULL UNIQUE,
              language TEXT NOT NULL DEFAULT 'unknown', language_confidence REAL NOT NULL DEFAULT 0);
            CREATE TABLE ai_decisions (id INTEGER PRIMARY KEY AUTOINCREMENT, job_id INTEGER NOT NULL, decided_at TEXT NOT NULL, match_score INTEGER NOT NULL,
              should_apply INTEGER NOT NULL, letter_required INTEGER NOT NULL, reason TEXT NOT NULL, resume_language TEXT NOT NULL DEFAULT '',
              apply_channel TEXT NOT NULL DEFAULT '', apply_result TEXT NOT NULL DEFAULT '');
            CREATE TABLE resumes (id INTEGER PRIMARY KEY AUTOINCREMENT, language TEXT NOT NULL UNIQUE, original_filename TEXT NOT NULL DEFAULT '',
              stored_path TEXT NOT NULL, file_sha256 TEXT NOT NULL, extracted_text TEXT NOT NULL DEFAULT '', analysis_json TEXT NOT NULL DEFAULT '', analysis_summary TEXT NOT NULL DEFAULT '', analyzed_at TEXT,
              provider TEXT NOT NULL DEFAULT '', model TEXT NOT NULL DEFAULT '', analysis_status TEXT NOT NULL DEFAULT 'none', analysis_error TEXT NOT NULL DEFAULT '',
              analysis_message TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            INSERT INTO settings(key, value) VALUES('candidate_name', 'Fulano de Tal');
            INSERT INTO jobs(source, title, url, first_seen_at, status, fingerprint) VALUES('t', 'Vaga antiga', 'https://x.test/1', '2026-01-01T00:00:00+00:00', 'applied', 'fp1');
            """);
        return path;
    }

    private static HashSet<string> Columns(string path, string table)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        return c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").ToHashSet();
    }

    [Fact]
    public void An_old_database_gets_the_new_columns_and_tables_and_keeps_its_data()
    {
        var dir = Path.Combine(Path.GetTempPath(), "odradek-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = OldDatabase(dir);
            var db = new Database(path);
            db.Initialize();

            Assert.Superset(new HashSet<string> { "search_filter_id", "search_remote", "reviewed_at" }, Columns(path, "jobs"));
            Assert.Superset(new HashSet<string> { "assessment_json", "scoring_version", "provider", "model" }, Columns(path, "ai_decisions"));
            Assert.Superset(new HashSet<string> { "profile_json", "profile_version" }, Columns(path, "resumes"));
            using var c = db.Open();
            var tables = c.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet();
            Assert.Superset(new HashSet<string> { "queue_jobs", "event_logs", "resume_files", "form_field_rules", "qpid_application_attempts", "qpid_application_results" }, tables);

            Assert.Equal("Fulano de Tal", c.ExecuteScalar<string>("SELECT value FROM settings WHERE key='candidate_name'"));
            Assert.Equal("applied", c.ExecuteScalar<string>("SELECT status FROM jobs WHERE fingerprint='fp1'"));
            Assert.True(c.ExecuteScalar<long>("SELECT COUNT(*) FROM settings") > 10, "os padrões das configurações entram");
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Opening_twice_changes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "odradek-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = OldDatabase(dir);
            var db = new Database(path);
            db.Initialize();
            string Fingerprint()
            {
                using var c = db.Open();
                var schema = string.Join("\n", c.Query<string>("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name"));
                var settings = string.Join("\n", c.Query<(string, string)>("SELECT key, value FROM settings ORDER BY key").Select(r => r.ToString()));
                return schema + "\n--\n" + settings;
            }
            var first = Fingerprint();
            db.Initialize();
            Assert.Equal(first, Fingerprint());
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_missing_database_is_created_from_scratch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "odradek-mig-" + Guid.NewGuid().ToString("N"), "pasta nova");
        try
        {
            var db = new Database(Path.Combine(dir, "jobs.db"));
            db.Initialize();
            using var c = db.Open();
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT COUNT(*) FROM jobs"));
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch (IOException) { } }
    }
}
