using Dapper;
using Microsoft.Data.Sqlite;

namespace Odradek.Core.Data;

/// <summary>
/// Esquema, configurações padrão e migrações do SQLite. Porta de <c>backend/data/repository.py</c> (e dos <c>ensure_table</c> de
/// <c>job_queue</c>, <c>panel_log</c>, <c>resume_storage</c>, <c>copilot_asks</c> e <c>qpid_applications</c>). Tudo é idempotente
/// (<c>IF NOT EXISTS</c>, colunas só se faltarem, marcadores em <c>settings</c>), então o app novo e o Python podem abrir o mesmo arquivo.
/// </summary>
public static class Schema
{
    // Tabelas na forma original; as colunas acrescentadas depois entram por LateColumns, na mesma ordem do Python.
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS jobs (
          id INTEGER PRIMARY KEY, source TEXT NOT NULL, source_id TEXT, title TEXT NOT NULL,
          company TEXT NOT NULL DEFAULT '', location TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT '',
          url TEXT NOT NULL, posted_at TEXT, first_seen_at TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'new',
          applied_at TEXT, notes TEXT NOT NULL DEFAULT '', fingerprint TEXT NOT NULL UNIQUE
        );
        CREATE INDEX IF NOT EXISTS jobs_status_idx ON jobs(status);
        CREATE INDEX IF NOT EXISTS jobs_seen_idx ON jobs(first_seen_at DESC);
        CREATE INDEX IF NOT EXISTS jobs_status_seen_idx ON jobs(status, first_seen_at DESC);
        CREATE TABLE IF NOT EXISTS runs (
          id INTEGER PRIMARY KEY, started_at TEXT NOT NULL, finished_at TEXT, state TEXT NOT NULL,
          found_count INTEGER NOT NULL DEFAULT 0, message TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS ai_decisions (
          id INTEGER PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          decided_at TEXT NOT NULL, match_score INTEGER NOT NULL, should_apply INTEGER NOT NULL,
          letter_required INTEGER NOT NULL, reason TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ai_decisions_job_latest_idx ON ai_decisions(job_id, id DESC);
        CREATE TABLE IF NOT EXISTS cover_letters (
          id INTEGER PRIMARY KEY, job_id INTEGER NOT NULL UNIQUE REFERENCES jobs(id) ON DELETE CASCADE,
          language TEXT NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL, body TEXT NOT NULL,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS resumes (
          id INTEGER PRIMARY KEY, language TEXT NOT NULL UNIQUE, original_filename TEXT NOT NULL DEFAULT '',
          stored_path TEXT NOT NULL, file_sha256 TEXT NOT NULL, extracted_text TEXT NOT NULL DEFAULT '',
          analysis_json TEXT NOT NULL DEFAULT '', analysis_summary TEXT NOT NULL DEFAULT '', analyzed_at TEXT,
          provider TEXT NOT NULL DEFAULT '', model TEXT NOT NULL DEFAULT '',
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS resume_files (
          resume_id INTEGER PRIMARY KEY REFERENCES resumes(id) ON DELETE CASCADE, content BLOB NOT NULL
        );
        CREATE TABLE IF NOT EXISTS form_field_rules (
          id INTEGER PRIMARY KEY, key TEXT NOT NULL, aliases TEXT NOT NULL DEFAULT '', mode TEXT NOT NULL DEFAULT 'text',
          value_from TEXT NOT NULL DEFAULT '', value TEXT NOT NULL DEFAULT '', sort_order INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS applications (
          id INTEGER PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          channel TEXT NOT NULL, recipient TEXT NOT NULL DEFAULT '', resume_id INTEGER, cover_letter_id INTEGER,
          status TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '', attempted_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS form_answers (
          id INTEGER PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          question TEXT NOT NULL, answer TEXT NOT NULL, provider TEXT NOT NULL DEFAULT '', model TEXT NOT NULL DEFAULT '',
          created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ai_answer_cache (
          id INTEGER PRIMARY KEY, question_norm TEXT NOT NULL UNIQUE, question_raw TEXT NOT NULL,
          kind TEXT NOT NULL DEFAULT 'open', answer TEXT NOT NULL, provider TEXT NOT NULL DEFAULT '',
          model TEXT NOT NULL DEFAULT '', hits INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS copilot_asks (
          id INTEGER PRIMARY KEY, job_id INTEGER, question TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending',
          answer TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, answered_at TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS copilot_asks_status_idx ON copilot_asks(status);
        CREATE TABLE IF NOT EXISTS queue_jobs (
          id INTEGER PRIMARY KEY, kind TEXT NOT NULL, payload TEXT NOT NULL, status TEXT NOT NULL,
          attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 40,
          next_run_at TEXT NOT NULL, expires_at TEXT NOT NULL, last_error TEXT NOT NULL DEFAULT '',
          result TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, updated_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS queue_jobs_status_idx ON queue_jobs(status, next_run_at);
        CREATE INDEX IF NOT EXISTS queue_jobs_kind_idx ON queue_jobs(kind);
        CREATE TABLE IF NOT EXISTS event_logs (
          id INTEGER PRIMARY KEY, created_at TEXT NOT NULL, level TEXT NOT NULL, source TEXT NOT NULL, message TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS event_logs_created_idx ON event_logs(created_at DESC);
        CREATE TABLE IF NOT EXISTS qpid_application_attempts (
          attempt_id TEXT PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          state TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS qpid_one_active_application
          ON qpid_application_attempts(job_id) WHERE state != 'failed-before-click';
        CREATE TABLE IF NOT EXISTS qpid_application_results (
          attempt_id TEXT PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          state TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL
        );
        """;

    // (tabela, coluna, definição), na ordem em que o Python as acrescenta.
    private static readonly (string Table, string Column, string Definition)[] LateColumns =
    [
        ("jobs", "language", "TEXT NOT NULL DEFAULT 'unknown'"),
        ("jobs", "language_confidence", "REAL NOT NULL DEFAULT 0"),
        ("resumes", "analysis_status", "TEXT NOT NULL DEFAULT 'none'"),
        ("resumes", "analysis_error", "TEXT NOT NULL DEFAULT ''"),
        ("resumes", "analysis_message", "TEXT NOT NULL DEFAULT ''"),
        ("copilot_asks", "hint", "TEXT NOT NULL DEFAULT ''"),
        ("copilot_asks", "kind", "TEXT NOT NULL DEFAULT 'text'"),
        ("copilot_asks", "file_path", "TEXT NOT NULL DEFAULT ''"),
        ("queue_jobs", "progress_log", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "resume_language", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "apply_channel", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "apply_result", "TEXT NOT NULL DEFAULT ''"),
        // NEW_COLUMNS do Python
        ("resumes", "profile_json", "TEXT NOT NULL DEFAULT ''"),
        ("resumes", "profile_version", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "assessment_json", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "scoring_version", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "provider", "TEXT NOT NULL DEFAULT ''"),
        ("ai_decisions", "model", "TEXT NOT NULL DEFAULT ''"),
        ("jobs", "search_filter_id", "TEXT NOT NULL DEFAULT ''"),
        ("jobs", "search_remote", "TEXT NOT NULL DEFAULT ''"),      // '1' = a vaga veio de uma busca com o filtro remoto (f_WT=2)
        ("jobs", "reviewed_at", "TEXT NOT NULL DEFAULT ''"),        // quando o usuário conferiu uma vaga descartada pela IA
    ];

    public static void Apply(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var statement in SplitStatements(Ddl)) db.Execute(statement, transaction: tx);

        foreach (var (table, column, definition) in LateColumns)
        {
            var existing = db.Query<string>($"SELECT name FROM pragma_table_info('{table}')", transaction: tx).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(column)) db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}", transaction: tx);
        }

        // Currículos com resumo contam como análise concluída.
        db.Execute("""
            UPDATE resumes SET analysis_status='ok',
                   analysis_message=COALESCE(NULLIF(analysis_message,''), 'Análise concluída com sucesso.')
            WHERE IFNULL(analysis_summary,'') != '' AND analysis_status IN ('none', '')
            """, transaction: tx);

        EnsureDefaultRules(db, tx);
        foreach (var (key, value) in Defaults.Settings)
            db.Execute("INSERT INTO settings(key,value) VALUES(@key,@value) ON CONFLICT(key) DO NOTHING", new { key, value }, tx);

        MigrateScoringV2Defaults(db, tx);
        MigrateFilterPrompts(db, tx);
        MigrateSelectionPrompt(db, tx);
        MigrateDiscardExcludedRegionJobs(db, tx);
        MigrateIndeedActor(db, tx);
    }

    /// <summary>Uma vez: quem ainda tem só o actor do LinkedIn (a lista padrão antiga) ganha o Indeed; listas editadas à mão não são tocadas.</summary>
    private static void MigrateIndeedActor(SqliteConnection db, SqliteTransaction tx)
    {
        if (Marked(db, tx, "indeed_actor_v2_migrated")) return;
        var value = db.ExecuteScalar<string?>("SELECT value FROM settings WHERE key='apify_actors_json'", transaction: tx);
        if (value is not null && Odradek.Core.Collector.ApifyActors.IsLegacyDefault(value))
            db.Execute("UPDATE settings SET value=@value WHERE key='apify_actors_json'", new { value = Odradek.Core.Collector.ApifyActors.DefaultsJson() }, tx);
        Mark(db, tx, "indeed_actor_v2_migrated");
    }

    private static IEnumerable<string> SplitStatements(string script) =>
        script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0);

    private static void EnsureDefaultRules(SqliteConnection db, SqliteTransaction tx)
    {
        var existing = db.Query<string>("SELECT key FROM form_field_rules", transaction: tx).Select(k => k.ToLowerInvariant()).ToHashSet();
        foreach (var rule in Defaults.FormRules)
        {
            if (existing.Contains(rule.Key.ToLowerInvariant())) continue;
            db.Execute("""
                INSERT INTO form_field_rules(key,aliases,mode,value_from,value,sort_order)
                VALUES(@Key,@Aliases,@Mode,@ValueFrom,@Value,@SortOrder)
                """, rule, tx);
        }
        db.Execute("UPDATE form_field_rules SET mode='salary' WHERE key='salary' AND mode='select'", transaction: tx);
    }

    private static bool Marked(SqliteConnection db, SqliteTransaction tx, string key) =>
        db.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@key", new { key }, tx) == "1";

    private static void Mark(SqliteConnection db, SqliteTransaction tx, string key) =>
        db.Execute("INSERT INTO settings(key,value) VALUES(@key,'1') ON CONFLICT(key) DO UPDATE SET value=excluded.value", new { key }, tx);

    /// <summary>Uma vez: o mínimo padrão antigo (65 ou 80) sobe para 70 na triagem v2.</summary>
    private static void MigrateScoringV2Defaults(SqliteConnection db, SqliteTransaction tx)
    {
        if (Marked(db, tx, "scoring_v2_migrated")) return;
        db.Execute("UPDATE settings SET value='70' WHERE key='minimum_match_score' AND value IN ('65','80')", transaction: tx);
        Mark(db, tx, "scoring_v2_migrated");
    }

    private static readonly string[] LegacyFilterPromptMarkers =
        ["MULTIPLE FILTERS (mandatory)", "KEYWORD BREADTH (mandatory)", "Produce 4-8 separate filters"];

    /// <summary>Uma vez: prompts salvos do modelo antigo (vários filtros) voltam ao padrão novo.</summary>
    private static void MigrateFilterPrompts(SqliteConnection db, SqliteTransaction tx)
    {
        if (Marked(db, tx, "filter_prompts_v3_migrated")) return;
        foreach (var key in new[] { "linkedin_filter_prompt", "transferable_prompt" })
        {
            var value = db.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@key", new { key }, tx);
            if (value is not null && LegacyFilterPromptMarkers.Any(m => value.Contains(m, StringComparison.Ordinal)))
                db.Execute("UPDATE settings SET value='' WHERE key=@key", new { key }, tx);
        }
        Mark(db, tx, "filter_prompts_v3_migrated");
    }

    /// <summary>Uma vez: o prompt de seleção antigo salvo como "personalizado" volta ao padrão atual.</summary>
    private static void MigrateSelectionPrompt(SqliteConnection db, SqliteTransaction tx)
    {
        if (Marked(db, tx, "selection_prompt_v2_migrated")) return;
        var value = db.ExecuteScalar<string?>("SELECT value FROM settings WHERE key='job_selection_prompt'", transaction: tx);
        if (value is not null && value.Contains("optimism calibration supplied below", StringComparison.Ordinal))
            db.Execute("UPDATE settings SET value='' WHERE key='job_selection_prompt'", transaction: tx);
        Mark(db, tx, "selection_prompt_v2_migrated");
    }

    private sealed class RegionRow
    {
        public long Id { get; set; }
        public string Status { get; set; } = "";
        public string? Location { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Uma vez: vagas antigas da Índia (golpe/vaga fantasma) saem de todas as listas. Candidaturas feitas e vagas salvas ficam como
    /// estão; as já ignoradas só ganham a marca de conferida; as demais viram ignoradas com o motivo.
    /// </summary>
    private static void MigrateDiscardExcludedRegionJobs(SqliteConnection db, SqliteTransaction tx)
    {
        if (Marked(db, tx, "india_jobs_discarded_v1")) return;
        var now = Clock.NowIso();
        var rows = db.Query<RegionRow>("SELECT id, status, location, title, description FROM jobs WHERE status NOT IN ('applied','saved')", transaction: tx).ToList();
        foreach (var row in rows)
        {
            var reason = RegionFilter.ExcludedReason(row.Location, row.Title, row.Description);
            if (reason.Length == 0) continue;
            if (row.Status == "ignored")
                db.Execute("UPDATE jobs SET reviewed_at=@now WHERE id=@id", new { now, id = row.Id }, tx);
            else
                db.Execute("UPDATE jobs SET status='ignored', reviewed_at=@now, notes=@notes WHERE id=@id",
                    new { now, notes = $"Região excluída: {reason}.", id = row.Id }, tx);
        }
        Mark(db, tx, "india_jobs_discarded_v1");
    }
}
