using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;

namespace Odradek.Core.Data;

/// <summary>Modelos de leitura das telas. Porta de <c>backend/web/panel_queries.py</c>: mesmos filtros, ordens, limites e cortes de texto.</summary>
public sealed partial class PanelQueries(Database db)
{
    private static readonly string JobSelect =
        "SELECT jobs.id, jobs.title, jobs.company, jobs.location, jobs.source, jobs.url, " +
        "jobs.posted_at, jobs.first_seen_at, jobs.status, jobs.description, jobs.notes, " +
        "cover_letters.body AS cover_letter, " + JobSort.MatchExpr + " AS match_score, " +
        "(SELECT assessment_json FROM ai_decisions WHERE ai_decisions.job_id = jobs.id ORDER BY id DESC LIMIT 1) AS assessment_json " +
        "FROM jobs LEFT JOIN cover_letters ON cover_letters.job_id=jobs.id ";

    // A visão geral só mostra título, link, empresa, local e status: sem descrição, carta nem auditoria.
    private const string DashboardJobSelect =
        "SELECT jobs.id, jobs.title, jobs.company, jobs.location, jobs.source, jobs.url, " +
        "jobs.posted_at, jobs.first_seen_at, jobs.status FROM jobs ";

    private sealed class JobRow
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string Company { get; set; } = "";
        public string Location { get; set; } = "";
        public string Source { get; set; } = "";
        public string Url { get; set; } = "";
        public string? PostedAt { get; set; }
        public string FirstSeenAt { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Description { get; set; }
        public string? Notes { get; set; }
        public string? CoverLetter { get; set; }
        public long? MatchScore { get; set; }
        public string? AssessmentJson { get; set; }
    }

    private static string SafeUrl(string? url) => Fingerprint.UrlScheme(url) is "http" or "https" ? url! : "";

    private static JobRecord ToRecord(JobRow row)
    {
        var contact = Emails.JobContacts(row.Description, row.Url, row.Company).Take(3).ToList(); // antes de truncar a descrição
        var letter = row.CoverLetter ?? "";
        return new JobRecord(row.Id, row.Title, row.Company, row.Location, row.Source, SafeUrl(row.Url), row.PostedAt, row.FirstSeenAt,
            row.Status, Text.Take(Text.StripTags(row.Description), 1800), row.Notes ?? "", letter, (int?)row.MatchScore, contact,
            AssessmentReader.Compact(row.AssessmentJson), letter.Trim().Length > 0);
    }

    private static JobRecord ToDashboardRecord(JobRow row) => new(row.Id, row.Title, row.Company, row.Location, row.Source,
        SafeUrl(row.Url), row.PostedAt, row.FirstSeenAt, row.Status, "", "", "", null, [], null, false);

    public DashboardData Dashboard()
    {
        using var connection = db.Open();
        var counts = connection.Query<(string Status, long N)>("SELECT status, COUNT(*) n FROM jobs GROUP BY status").ToDictionary(r => r.Status, r => r.N);
        var jobs = connection.Query<JobRow>(DashboardJobSelect + "ORDER BY jobs.first_seen_at DESC LIMIT 10").Select(ToDashboardRecord).ToList();
        var runs = connection.Query<RunRecord>(
            "SELECT id AS Id, started_at AS StartedAt, finished_at AS FinishedAt, state AS State, found_count AS FoundCount, message AS Message " +
            "FROM runs ORDER BY id DESC LIMIT 8").ToList();
        return new DashboardData(counts, jobs, runs);
    }

    public JobPage Jobs(int page = 1, int pageSize = 15, string status = "worth", int minMatch = 0, string sort = "", string dateFrom = "", string dateTo = "")
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 50);
        minMatch = Math.Clamp(minMatch, 0, 100);
        status = Defaults.IsStatus(status) ? status : "worth";
        sort = JobSort.Normalize(sort);
        if (dateFrom.Length > 0 && dateTo.Length > 0 && string.CompareOrdinal(dateFrom, dateTo) > 0) (dateFrom, dateTo) = (dateTo, dateFrom);
        var (lo, hi) = JobSort.DateBoundsUtc(dateFrom, dateTo);

        var where = "jobs.status = @status";
        var parameters = new DynamicParameters();
        parameters.Add("status", status);
        if (minMatch > 0) { where += $" AND COALESCE({JobSort.MatchExpr}, 0) >= @minMatch"; parameters.Add("minMatch", minMatch); }
        if (lo is not null) { where += " AND jobs.first_seen_at >= @lo"; parameters.Add("lo", lo); }
        if (hi is not null) { where += " AND jobs.first_seen_at < @hi"; parameters.Add("hi", hi); }

        using var connection = db.Open();
        var total = connection.ExecuteScalar<long>($"SELECT COUNT(*) n FROM jobs WHERE {where}", parameters);
        var pages = (int)Math.Max(1, (total + pageSize - 1) / pageSize);
        page = Math.Min(page, pages);
        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);
        var rows = connection.Query<JobRow>(JobSelect + $"WHERE {where} ORDER BY {JobSort.OrderSql(sort)} LIMIT @limit OFFSET @offset", parameters);
        return new JobPage(rows.Select(ToRecord).ToList(), total, page, pages, pageSize, new JobFilters(status, minMatch, sort, dateFrom, dateTo));
    }

    // Abas de "Não selecionadas": (filtro SQL, ordem padrão).
    private static readonly Dictionary<string, (string Where, string Order)> TriageTabs = new()
    {
        // Descartadas pela IA e ainda não conferidas: a decisão mais recente reprovou a vaga. Vagas ignoradas à mão não entram.
        ["discarded"] = (
            "jobs.status = 'ignored' AND COALESCE(jobs.reviewed_at, '') = '' AND " +
            "(SELECT should_apply FROM ai_decisions WHERE ai_decisions.job_id = jobs.id ORDER BY id DESC LIMIT 1) = 0",
            "COALESCE(" + JobSort.MatchExpr + ", 0) DESC, jobs.id DESC"),
        // Ainda sem decisão da IA: coletadas ou na fila de triagem.
        ["pending"] = ("jobs.status IN ('new', 'review')", "jobs.first_seen_at DESC, jobs.id DESC"),
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public TriagePage Triage(string tab = "discarded", int page = 1, int pageSize = 15, int minMatch = 0, string sort = "",
        string dateFrom = "", string dateTo = "", string q = "")
    {
        tab = TriageTabs.ContainsKey(tab) ? tab : "discarded";
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 50);
        minMatch = Math.Clamp(minMatch, 0, 100);
        sort = JobSort.Normalize(sort);
        if (dateFrom.Length > 0 && dateTo.Length > 0 && string.CompareOrdinal(dateFrom, dateTo) > 0) (dateFrom, dateTo) = (dateTo, dateFrom);
        var (lo, hi) = JobSort.DateBoundsUtc(dateFrom, dateTo);

        var (tabWhere, defaultOrder) = TriageTabs[tab];
        var clauses = new List<string> { tabWhere };
        var parameters = new DynamicParameters();
        if (minMatch > 0 && tab == "discarded") { clauses.Add($"COALESCE({JobSort.MatchExpr}, 0) >= @minMatch"); parameters.Add("minMatch", minMatch); }
        if (lo is not null) { clauses.Add("jobs.first_seen_at >= @lo"); parameters.Add("lo", lo); }
        if (hi is not null) { clauses.Add("jobs.first_seen_at < @hi"); parameters.Add("hi", hi); }

        var terms = Spaces().Split(Text.Take((q ?? "").Trim().ToLowerInvariant(), 100)).Where(t => t.Length > 0).Take(6).ToList();
        for (var i = 0; i < terms.Count; i++)
        {
            clauses.Add($"LOWER(COALESCE(jobs.title, '') || ' ' || COALESCE(jobs.company, '') || ' ' || COALESCE(jobs.location, '')) LIKE @term{i} ESCAPE '!'");
            parameters.Add($"term{i}", "%" + terms[i].Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%");
        }
        var filtered = string.Join(" AND ", clauses);
        var order = sort.Length > 0 ? JobSort.OrderSql(sort) : defaultOrder;

        using var connection = db.Open();
        var counts = TriageTabs.ToDictionary(t => t.Key, t => connection.ExecuteScalar<long>($"SELECT COUNT(*) n FROM jobs WHERE {t.Value.Where}"));
        var total = connection.ExecuteScalar<long>($"SELECT COUNT(*) n FROM jobs WHERE {filtered}", parameters);
        var pages = (int)Math.Max(1, (total + pageSize - 1) / pageSize);
        page = Math.Min(page, pages);
        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);
        var rows = connection.Query<JobRow>(JobSelect + $"WHERE {filtered} ORDER BY {order} LIMIT @limit OFFSET @offset", parameters);
        return new TriagePage(tab, rows.Select(ToRecord).ToList(), total, page, pages, pageSize, counts,
            new TriageFilters(minMatch, sort, dateFrom, dateTo, string.Join(' ', terms)));
    }

    /// <summary>Marca vagas descartadas pela IA como conferidas (saem da aba). Só mexe em vagas ignoradas.</summary>
    public int MarkReviewed(IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return 0;
        int changed;
        using (var connection = db.Open())
            changed = connection.Execute(
                "UPDATE jobs SET reviewed_at=@now WHERE id IN @ids AND status='ignored' AND COALESCE(reviewed_at, '')=''", new { now = Clock.NowIso(), ids = list });
        if (changed > 0) db.Bus.Publish(new JobsChanged(list));
        return Math.Max(changed, 0);
    }

    // ---- fila ------------------------------------------------------------------------------------------------------
    private const int ProgressTailChars = 600;

    private sealed class QueueRow
    {
        public long Id { get; set; }
        public string Kind { get; set; } = "";
        public string Payload { get; set; } = "";
        public string Status { get; set; } = "";
        public long Attempts { get; set; }
        public long MaxAttempts { get; set; }
        public string NextRunAt { get; set; } = "";
        public string? LastError { get; set; }
        public string? Result { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public long? ProgressLen { get; set; }
        public string? ProgressTail { get; set; }
    }

    private sealed class ContextJob { public long Id { get; set; } public string? Title { get; set; } public string? Company { get; set; } public string? Location { get; set; } public string? Url { get; set; } public string? Status { get; set; } }
    private sealed class ContextDecision { public long JobId { get; set; } public string? DecidedAt { get; set; } public long? MatchScore { get; set; } public long ShouldApply { get; set; } public string? Reason { get; set; } public string? Provider { get; set; } public string? Model { get; set; } }

    private static JsonObject PayloadOf(string? payload)
    {
        try { return JsonNode.Parse(payload ?? "") as JsonObject ?? []; }
        catch (JsonException) { return []; }
    }

    private static long JobIdOf(JsonObject payload)
    {
        var node = payload["job_id"];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return (long)d;
            if (value.TryGetValue<string>(out var s) && long.TryParse(s.Trim(), out var parsed)) return parsed;
        }
        return 0;
    }

    private static Dictionary<string, object?>? Subject(string kind, JsonObject payload, IReadOnlyDictionary<long, ContextJob> jobs)
    {
        switch (kind)
        {
            case "job_apply":
                var id = JobIdOf(payload);
                if (!jobs.TryGetValue(id, out var job))
                    return new() { ["type"] = "job", ["job_id"] = id, ["title"] = "", ["company"] = "", ["location"] = "", ["url"] = "", ["job_status"] = "", ["missing"] = true };
                return new()
                {
                    ["type"] = "job", ["job_id"] = id, ["title"] = job.Title ?? "", ["company"] = job.Company ?? "", ["location"] = job.Location ?? "",
                    ["url"] = SafeUrl(job.Url), ["job_status"] = job.Status ?? "", ["missing"] = false,
                };
            case "resume_analysis": return new() { ["type"] = "resume", ["language"] = Text.PyStr(payload["language"]) };
            case "worth_reverify": return new() { ["type"] = "worth", ["scheduled_date"] = Text.PyStr(payload["scheduled_date"]) };
            default: return null;
        }
    }

    /// <summary>Decisões antigas gravaram provedor "gemini" com modelo "tokenharbor/x": corrige só na exibição.</summary>
    public static (string Provider, string Model) SplitModelLabel(string provider, string label)
    {
        var slash = (label ?? "").IndexOf('/');
        if (slash >= 0 && label![..slash] == "tokenharbor" && slash + 1 < label.Length) return ("tokenharbor", label[(slash + 1)..]);
        return (provider, label ?? "");
    }

    public QueuePage Queue()
    {
        using var connection = db.Open();
        var records = connection.Query<QueueRow>(
            "SELECT id, kind, payload, status, attempts, max_attempts, next_run_at, expires_at, last_error, result, created_at, updated_at, " +
            "LENGTH(progress_log) AS progress_len, " +
            $"CASE WHEN LENGTH(progress_log) > {ProgressTailChars} THEN SUBSTR(progress_log, LENGTH(progress_log) - {ProgressTailChars - 1}) ELSE progress_log END AS progress_tail " +
            "FROM queue_jobs ORDER BY id DESC LIMIT 100").ToList();
        var counts = connection.Query<(string Status, long N)>("SELECT status, COUNT(*) AS n FROM queue_jobs GROUP BY status").ToDictionary(r => r.Status, r => r.N);
        var payloads = records.Select(r => PayloadOf(r.Payload)).ToList();
        var jobIds = payloads.Select(JobIdOf).Where(i => i != 0).Distinct().OrderBy(i => i).ToList();
        var jobs = new Dictionary<long, ContextJob>();
        var decisions = new Dictionary<long, ContextDecision>();
        if (jobIds.Count > 0)
        {
            foreach (var row in connection.Query<ContextJob>("SELECT id, title, company, location, url, status FROM jobs WHERE id IN @ids", new { ids = jobIds })) jobs[row.Id] = row;
            foreach (var row in connection.Query<ContextDecision>(
                         "SELECT job_id, decided_at, match_score, should_apply, reason, provider, model FROM ai_decisions " +
                         "WHERE id IN (SELECT MAX(id) FROM ai_decisions WHERE job_id IN @ids GROUP BY job_id)", new { ids = jobIds })) decisions[row.JobId] = row;
        }

        var items = new List<QueueItem>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var tail = record.ProgressTail ?? "";
            var truncated = (record.ProgressLen ?? 0) > tail.Length;
            var lines = Text.SplitLines(tail);
            if (truncated && lines.Count > 0) lines.RemoveAt(0); // a primeira linha da cauda pode estar cortada ao meio
            var subject = Subject(record.Kind, payloads[i], jobs);
            QueueDecision? decision = null;
            if (subject is not null && (string?)subject["type"] == "job" && decisions.TryGetValue((long)subject["job_id"]!, out var d))
            {
                var (provider, model) = SplitModelLabel(d.Provider ?? "", d.Model ?? "");
                decision = new QueueDecision(d.MatchScore is null ? null : (int)d.MatchScore, d.ShouldApply != 0,
                    Text.Take(d.Reason ?? "", 1000), model, provider, d.DecidedAt ?? "");
            }
            var outcome = !string.IsNullOrEmpty(record.LastError) ? record.LastError : record.Result ?? "";
            items.Add(new QueueItem(record.Id, record.Kind, record.Status, record.Attempts, record.MaxAttempts, record.NextRunAt, record.CreatedAt,
                record.UpdatedAt, Text.Take(lines.Count > 0 ? lines[^1] : "", 280), string.Join('\n', lines), truncated,
                Text.Take(outcome, 500), subject, decision));
        }
        return new QueuePage(counts, items);
    }

    /// <summary>Histórico completo (últimas 80 linhas / 8000 caracteres) de um item da fila; <c>null</c> se não existir.</summary>
    public string? QueueProgress(long queueId)
    {
        using var connection = db.Open();
        var found = connection.Query<string?>("SELECT progress_log FROM queue_jobs WHERE id=@queueId", new { queueId }).ToList();
        if (found.Count == 0) return null;
        var lines = Text.SplitLines(found[0]);
        return Text.TakeLast(string.Join('\n', lines.Skip(Math.Max(0, lines.Count - 80))), 8000);
    }

    /// <summary>Auditoria compacta (checklist) da vaga de um item da fila; <c>null</c> se não houver.</summary>
    public Assessment? QueueAssessment(long queueId)
    {
        using var connection = db.Open();
        var row = connection.QueryFirstOrDefault<(string Kind, string Payload)?>("SELECT kind, payload FROM queue_jobs WHERE id=@queueId", new { queueId });
        if (row is null || row.Value.Kind != "job_apply") return null;
        var jobId = JobIdOf(PayloadOf(row.Value.Payload));
        if (jobId == 0) return null;
        var raw = connection.QueryFirstOrDefault<string?>("SELECT assessment_json FROM ai_decisions WHERE job_id=@jobId ORDER BY id DESC LIMIT 1", new { jobId });
        return AssessmentReader.Compact(raw);
    }

    public sealed record FormRule(string Key, string Aliases, string Mode, string ValueFrom, string Value);

    /// <summary>Regras de preenchimento de formulário (a extensão usa as mesmas). Ordem: <c>sort_order</c>, depois <c>id</c> (não conferida com o Python).</summary>
    public IReadOnlyList<FormRule> FormRules()
    {
        using var connection = db.Open();
        return connection.Query<FormRule>(
            "SELECT key AS Key, aliases AS Aliases, mode AS Mode, value_from AS ValueFrom, value AS Value FROM form_field_rules ORDER BY sort_order, id").ToList();
    }

    // ---- logs, currículos, métricas --------------------------------------------------------------------------------
    public IReadOnlyList<LogEntry> Logs(int limit = 200) => new EventLogStore(db).List(limit);

    public Dictionary<string, ResumeInfo?> Resumes()
    {
        using var connection = db.Open();
        // Só o que a tela usa: o texto extraído do PDF (dezenas de KB) não sai do banco.
        var found = connection.Query<ResumeInfo>(
            "SELECT language AS Language, original_filename AS Filename, analysis_status AS Status, analysis_message AS Message, " +
            "analysis_error AS Error, analysis_summary AS Summary, analysis_json AS AnalysisJson, analyzed_at AS AnalyzedAt, " +
            "provider AS Provider, model AS Model FROM resumes WHERE language IN ('pt','en')").ToDictionary(r => r.Language);
        return new Dictionary<string, ResumeInfo?> { ["pt"] = found.GetValueOrDefault("pt"), ["en"] = found.GetValueOrDefault("en") };
    }

    private sealed class MetricRow
    {
        public string FilterId { get; set; } = "";
        public long Collected { get; set; }
        public long? Last7d { get; set; }
        public long? Last30d { get; set; }
        public long? Triaged { get; set; }
        public long? Approved { get; set; }
        public long? IgnoredByUser { get; set; }
    }

    /// <summary>Rendimento por filtro de busca: quantas vagas trouxe e quantas a triagem aprovou.</summary>
    public Dictionary<string, FilterMetric> FilterMetrics()
    {
        var now = DateTime.UtcNow;
        using var connection = db.Open();
        var rows = connection.Query<MetricRow>(
            """
            SELECT j.search_filter_id AS filter_id, COUNT(*) AS collected,
                   SUM(CASE WHEN j.first_seen_at >= @since7 THEN 1 ELSE 0 END) AS last_7d,
                   SUM(CASE WHEN j.first_seen_at >= @since30 THEN 1 ELSE 0 END) AS last_30d,
                   SUM(CASE WHEN d.id IS NOT NULL THEN 1 ELSE 0 END) AS triaged,
                   SUM(CASE WHEN d.should_apply = 1 THEN 1 ELSE 0 END) AS approved,
                   SUM(CASE WHEN d.should_apply = 1 AND j.status = 'ignored' THEN 1 ELSE 0 END) AS ignored_by_user
            FROM jobs j
            LEFT JOIN ai_decisions d ON d.id = (SELECT MAX(x.id) FROM ai_decisions x WHERE x.job_id = j.id)
            WHERE j.search_filter_id <> ''
            GROUP BY j.search_filter_id
            """, new { since7 = Clock.ToIso(now.AddDays(-7)), since30 = Clock.ToIso(now.AddDays(-30)) });
        return rows.ToDictionary(r => r.FilterId, r =>
        {
            var triaged = r.Triaged ?? 0;
            var approved = r.Approved ?? 0;
            double? rate = triaged > 0 ? (double)approved / triaged : null;
            // Só um aviso: o painel nunca desativa filtros sozinho.
            return new FilterMetric(r.Collected, r.Last7d ?? 0, r.Last30d ?? 0, triaged, approved, r.IgnoredByUser ?? 0, rate,
                triaged >= 20 && rate is < 0.1);
        });
    }
}
