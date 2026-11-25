using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Odradek.Core.Actions;
using Odradek.Core.Candidate;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.Core.Api;

/// <summary>
/// Rotas <c>/api/qpid/*</c> (contrato de candidatura v2, <c>web/qpid_applications.py</c> e <c>api_asgi.py</c>). Cada método devolve o que o
/// FastAPI devolvia: mesmo status, mesmas chaves na mesma ordem e as mesmas mensagens em português.
/// </summary>
public sealed partial class QpidApi(Database db, JobCollector collector, Func<HttpClient>? http = null)
{
    private const string Tables = """
        CREATE TABLE IF NOT EXISTS qpid_application_attempts (
          attempt_id TEXT PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          state TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS qpid_one_active_application ON qpid_application_attempts(job_id) WHERE state != 'failed-before-click';
        CREATE TABLE IF NOT EXISTS qpid_application_results (
          attempt_id TEXT PRIMARY KEY, job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          state TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL)
        """;

    [GeneratedRegex(@"^[a-zA-Z0-9-]{20,80}$")] private static partial Regex AttemptId();
    [GeneratedRegex(@"^[a-f0-9]{64}$")] private static partial Regex Sha256Hex();
    [GeneratedRegex("salary|availability|contract|pretens", RegexOptions.IgnoreCase)] private static partial Regex PreferenceKey();

    private static void EnsureTables(SqliteConnection connection, SqliteTransaction? tx = null) => connection.Execute(Tables, transaction: tx);

    // ------------------------------------------------------------------ painel do Qpid

    public static JsonObject SnapshotJson(CollectorSnapshot s) => new()
    {
        ["state"] = s.State, ["message"] = s.Message, ["last_run"] = s.LastRun, ["last_found"] = s.LastFound, ["next_run_at"] = s.NextRunAt, ["max_apply"] = s.MaxApply,
    };

    public ApiResult Status() => ApiResult.Ok(new JsonObject { ["collector"] = SnapshotJson(collector.Snapshot()) });

    public static ApiResult Capabilities() => ApiResult.Ok(new JsonObject
    {
        ["version"] = 2, ["application_contract"] = 2,
        ["features"] = new JsonArray("profile-facts-v1", "application-reservation-v1", "application-result-v1", "collector-apply-limit-v1", "linkedin-import-v1"),
    });

    /// <summary>Vagas lidas pelo Qpid numa sessão logada do LinkedIn (com descrição): entram como novas e seguem para a triagem.</summary>
    public Task<ApiResult> ImportLinkedInAsync(JsonObject body, CancellationToken ct = default) => LinkedInImport.HandleAsync(db, body, http, ct);

    public ApiResult Queue(int limit, bool fromStart)
    {
        List<JsonObject> rows;
        JsonObject counts = new();
        using (var connection = db.Open())
        {
            rows = Rows.Query(connection, $"SELECT id, kind, status, progress_log FROM queue_jobs ORDER BY id {(fromStart ? "ASC" : "DESC")} LIMIT @limit", ("@limit", limit));
            foreach (var row in connection.Query<(string Status, long N)>("SELECT status, COUNT(*) AS n FROM queue_jobs GROUP BY status")) counts[row.Status] = row.N;
        }
        var items = rows.Select(row => new JsonObject
        {
            ["id"] = row["id"]!.DeepClone(), ["kind"] = row["kind"]!.DeepClone(), ["status"] = row["status"]!.DeepClone(),
            ["last_step"] = Text.Take(Text.SplitLines(Text.PyStr(row["progress_log"])).LastOrDefault() ?? "", 280),
        }).ToList();
        if (!fromStart) items.Reverse();
        return ApiResult.Ok(new JsonObject { ["counts"] = counts, ["items"] = new JsonArray(items.ToArray<JsonNode?>()) });
    }

    public ApiResult Logs(int limit, bool fromStart)
    {
        List<JsonObject> rows;
        using (var connection = db.Open())
            rows = Rows.Query(connection, $"SELECT * FROM event_logs ORDER BY id {(fromStart ? "ASC" : "DESC")} LIMIT @limit", ("@limit", limit));
        if (!fromStart) rows.Reverse();
        return ApiResult.Ok(new JsonObject { ["items"] = new JsonArray(rows.ToArray<JsonNode?>()) });
    }

    /// <summary>Inicia ou para a coleta. No <c>start</c> o corpo opcional pode trazer <c>max_apply</c> (inteiro de 1 a 500).</summary>
    public ApiResult CollectorAction(string action, string? rawBody)
    {
        int? maxApply = null;
        if (action == "start" && !string.IsNullOrWhiteSpace(rawBody))
        {
            JsonNode? body;
            try { body = JsonNode.Parse(rawBody); }
            catch (JsonException) { return ApiResult.Error("Corpo inválido"); }
            var value = body is JsonObject o ? o["max_apply"] : null;
            if (value is not null)
            {
                if (!Contract.IsInt(value, out var number) || number is < 1 or > 500) return ApiResult.Error("max_apply deve ser um inteiro de 1 a 500");
                maxApply = (int)number;
            }
        }
        string notice;
        if (action == "start") notice = collector.Start(maxApply) ? "Coleta iniciada." : "A coleta já está em execução.";
        else if (action == "stop") { collector.Stop(); notice = "Solicitação para parar enviada."; }
        else return ApiResult.Error("Ação inválida", 404);
        return ApiResult.Ok(new JsonObject { ["ok"] = true, ["notice"] = notice, ["collector"] = SnapshotJson(collector.Snapshot()) });
    }

    // ------------------------------------------------------------------ ignorar em lote

    private static BulkIgnoreCriteria Criteria(JsonObject body)
    {
        string[] fields = ["job_id", "country", "found_from", "found_to", "posted_from", "posted_to", "match_min", "match_max"];
        if (body.Any(kv => !fields.Contains(kv.Key) && kv.Key != "all")) throw new ActionException("Filtros inválidos.");
        var values = new Dictionary<string, string>();
        foreach (var field in fields)
        {
            if (!body.TryGetPropertyValue(field, out var node)) { values[field] = ""; continue; }
            values[field] = Contract.String(node) ?? throw new ActionException("Filtros inválidos.");
        }
        var all = false;
        if (body.TryGetPropertyValue("all", out var allNode))
            all = allNode is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : throw new ActionException("Filtros inválidos.");
        return new BulkIgnoreCriteria(values["job_id"], values["country"], values["found_from"], values["found_to"], values["posted_from"], values["posted_to"],
            values["match_min"], values["match_max"], all);
    }

    public ApiResult BulkIgnorePreview(JsonObject body)
    {
        try { return ApiResult.Ok(new JsonObject { ["count"] = new JobActions(db).BulkIgnorePreview(Criteria(body)) }); }
        catch (ActionException ex) { return ApiResult.Error(ex.Message); }
    }

    public ApiResult BulkIgnoreApply(JsonObject body)
    {
        var actions = new JobActions(db);
        BulkIgnoreCriteria criteria;
        long expected;
        try
        {
            if (!Contract.IsInt(body["expected_count"], out expected) || expected < 1) throw new ActionException("Confira a quantidade antes de ignorar.");
            var rest = new JsonObject();
            foreach (var (key, value) in body) if (key != "expected_count") rest[key] = value?.DeepClone();
            criteria = Criteria(rest);
            if (actions.BulkIgnorePreview(criteria) != expected) return ApiResult.Error("A quantidade de vagas mudou. Confira novamente antes de ignorar.", 409);
        }
        catch (ActionException ex) { return ApiResult.Error(ex.Message); }
        try { actions.BulkIgnoreApply(criteria, (int)expected); }
        catch (ActionException ex) { return ApiResult.Error(ex.Message, 409); } // mudou entre a conferência e a gravação
        return ApiResult.Ok(new JsonObject { ["count"] = expected, ["notice"] = $"{expected} vaga(s) ignorada(s)." });
    }

    // ------------------------------------------------------------------ candidaturas

    public ApiResult Candidates(int limit)
    {
        using var connection = db.Open();
        var rows = Rows.Query(connection,
            $"SELECT jobs.id, jobs.title, jobs.company, jobs.status, {JobSort.MatchExpr} AS match_score FROM jobs WHERE jobs.status IN ('worth','saved') ORDER BY jobs.id DESC LIMIT @limit",
            ("@limit", limit));
        return ApiResult.Ok(new JsonObject { ["items"] = new JsonArray(rows.ToArray<JsonNode?>()) });
    }

    private static JsonNode? RowValue(JsonObject row, string key) => row.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>Contexto da candidatura: regras e fatos do perfil resolvidos, trechos do currículo e as versões que o Qpid usa para reservar.</summary>
    public ApiResult Context(long jobId, string language) => BuildContext(jobId, language, out var context) ?? ApiResult.Ok(context!);

    private ApiResult? BuildContext(long jobId, string language, out JsonObject? context)
    {
        context = null;
        var cfg = new SettingsStore(db).All();
        JsonObject? job, resume, letter, decision = null;
        List<JsonObject> rules;
        using (var connection = db.Open())
        {
            job = Rows.Query(connection, "SELECT id, title, company, url, status FROM jobs WHERE id=@id", ("@id", jobId)).FirstOrDefault();
            if (job is null) return ApiResult.Error("Vaga não encontrada", 404);
            if (Text.PyStr(job["status"]) == "applied") return ApiResult.Error("Vaga já marcada como enviada.", 409);
            rules = Rows.Query(connection, "SELECT * FROM form_field_rules ORDER BY sort_order, id");
            resume = Rows.Query(connection, "SELECT * FROM resumes WHERE language=@language", ("@language", language)).FirstOrDefault();
            letter = Rows.Query(connection, "SELECT body FROM cover_letters WHERE job_id=@id", ("@id", jobId)).FirstOrDefault();
            try { decision = Rows.Query(connection, "SELECT assessment_json FROM ai_decisions WHERE job_id=@id ORDER BY id DESC LIMIT 1", ("@id", jobId)).FirstOrDefault(); }
            catch (SqliteException) { decision = null; }
        }
        var resolved = new JsonArray();
        var facts = new JsonArray();

        void Append(string id, IEnumerable<string> aliases, string value, string source, string kind = "fact", string scope = "profile", bool sourceOnly = false, JsonObject? extra = null)
        {
            var item = new JsonObject
            {
                ["id"] = id, ["aliases"] = new JsonArray(aliases.Select(a => (JsonNode?)a).ToArray()), ["value"] = value, ["source"] = source,
                ["kind"] = kind, ["language"] = language, ["scope"] = scope,
            };
            if (sourceOnly) item["sourceOnly"] = true;
            if (extra is not null) foreach (var (key, node) in extra) item[key] = node?.DeepClone();
            item["version"] = Contract.Digest(item); // o resumo é calculado antes de "version" entrar
            resolved.Add(item.DeepClone());
            facts.Add(item.DeepClone());
        }

        foreach (var rule in rules)
        {
            var mode = Text.PyStr(rule["mode"]).ToLowerInvariant();
            if (mode is not ("text" or "select" or "cover_letter")) continue;
            var valueFrom = Text.PyStr(rule["value_from"]);
            var value = mode == "cover_letter" ? (letter is null ? "" : Text.PyStr(letter["body"]))
                : valueFrom.Length > 0 ? cfg.GetValueOrDefault(valueFrom, "") : Text.PyStr(rule["value"]);
            if (value.Trim().Length == 0) continue;
            var aliases = Text.PyStr(rule["aliases"]).Replace(';', ',').Replace('\n', ',').Split(',').Select(a => a.Trim()).Where(a => a.Length > 0);
            var key = Text.PyStr(rule["key"]);
            Append($"form-rule:{Text.PyStrRaw(rule["id"])}:{key}", aliases, value, valueFrom.Length > 0 ? valueFrom : "form-rule",
                PreferenceKey().IsMatch(key) ? "preference" : "fact", mode == "cover_letter" ? "job" : "profile");
        }
        var name = (cfg.GetValueOrDefault("candidate_name") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (name.Length > 0)
        {
            Append("candidate:first_name", ["first name", "primeiro nome"], name[0], "candidate_name");
            if (name.Length > 1) Append("candidate:last_name", ["last name", "sobrenome"], string.Join(' ', name[1..]), "candidate_name");
        }
        // O texto do currículo enviado é evidência, nunca uma regra de preenchimento direto nem um resumo de IA: trechos com as palavras do PDF,
        // por seção e cargo; os que sustentam requisitos desta vaga vêm primeiro.
        if (resume is not null && Text.PyStr(resume["extracted_text"]).Trim().Length > 0)
        {
            var excerpts = ResumeExcerpts.Of(Text.PyStr(resume["extracted_text"]).Trim(), ResumeProfile.ParseProfile(Text.PyStr(resume["profile_json"])), language: language);
            var backed = BackedRequirements(decision);
            var ranked = excerpts.Select((excerpt, index) =>
            {
                var norm = Text.NormalizeText(excerpt.Text);
                var matched = backed.Where(b => norm.Contains(b.Quote, StringComparison.Ordinal)).Select(b => b.Text).Distinct().Take(3).ToList();
                return (Rank: matched.Count > 0 ? 0 : 1, Index: index, Excerpt: excerpt, Matched: matched);
            }).OrderBy(r => r.Rank).ThenBy(r => r.Index).ToList();
            for (var number = 0; number < ranked.Count; number++)
            {
                var (_, _, excerpt, matched) = ranked[number];
                var extra = new JsonObject { ["section"] = excerpt.Section, ["sourceLanguage"] = language };
                if (matched.Count > 0) extra["matched_requirements"] = new JsonArray(matched.Select(m => (JsonNode?)m).ToArray());
                Append($"resume:{language}:excerpt:{number}", [excerpt.Label], excerpt.Text, "uploaded-resume", sourceOnly: true, extra: extra);
            }
        }
        var resumeVersion = resume is null ? null : Contract.Digest(resume);
        var profileFacts = new JsonArray(facts.OfType<JsonObject>().Where(f => Text.PyStr(f["scope"]) == "profile").Select(f => (JsonNode?)f.DeepClone()).ToArray());
        context = new JsonObject
        {
            ["contract_version"] = 2,
            ["context_version"] = Contract.Digest(new JsonArray(facts.DeepClone(), resumeVersion)),
            ["job"] = job,
            ["rules"] = resolved,
            ["profile"] = new JsonObject { ["schema"] = 1, ["version"] = Contract.Digest(profileFacts), ["facts"] = facts },
            ["resume"] = resume is not null,
            ["resume_meta"] = resume is null ? null : new JsonObject { ["id"] = RowValue(resume, "id")?.DeepClone(), ["language"] = language, ["version"] = resumeVersion },
            ["language"] = language,
        };
        return null;
    }

    /// <summary>Requisitos e responsabilidades da última triagem com citação do currículo (met/transferable/partial): (citação normalizada, texto até 120).</summary>
    private static List<(string Quote, string Text)> BackedRequirements(JsonObject? decision)
    {
        var backed = new List<(string, string)>();
        try
        {
            if (decision is null || Text.PyStr(decision["assessment_json"]).Length == 0) return backed;
            if (JsonNode.Parse(Text.PyStr(decision["assessment_json"])) is not JsonObject audit) return [];
            foreach (var group in new[] { "requirements", "responsibilities" })
            {
                if (audit[group] is null) continue;
                if (audit[group] is not JsonArray entries) return []; // list(dict) daria as chaves e o .get quebraria: o Python descarta tudo
                foreach (var node in entries)
                {
                    if (node is not JsonObject entry) return [];
                    var quote = Text.NormalizeText(Text.PyStr(entry["resume_quote"]));
                    if (quote.Length > 0 && Text.PyStr(entry["status"]) is "met" or "transferable" or "partial")
                        backed.Add((quote, Text.Take(Text.PyStrRaw(entry["text"] ?? JsonValue.Create("")), 120)));
                }
            }
        }
        catch (JsonException) { return []; }
        return backed;
    }

    public ApiResult ResumeFile(long jobId, string language)
    {
        using var connection = db.Open();
        if (connection.ExecuteScalar<long?>("SELECT id FROM jobs WHERE id=@jobId", new { jobId }) is null) return ApiResult.Error("Vaga não encontrada", 404);
        if (connection.ExecuteScalar<long?>("SELECT id FROM resumes WHERE language=@language", new { language }) is null)
            return ApiResult.Error("Currículo não cadastrado neste idioma.", 404);
        return ResumeActions.ReadPdf(connection, language) is { } pdf ? ApiResult.Pdf(pdf) : ApiResult.Error("Arquivo de currículo indisponível.", 404);
    }

    private sealed record AttemptRow(long JobId, string Payload);

    private const string ReserveInvalid = "Reserva inválida.";

    public ApiResult Reserve(long jobId, JsonObject body)
    {
        var attempt = Contract.String(body["attempt_id"]);
        var language = Contract.String(body["language"]);
        var digest = Contract.String(body["digest"]);
        if (!Contract.EqualsOne(body["version"]) || attempt is null || !AttemptId().IsMatch(attempt) || language is not ("pt" or "en") || digest is null || !Sha256Hex().IsMatch(digest))
            return ApiResult.Error(ReserveInvalid);
        if (BuildContext(jobId, language, out var candidate) is { } failure) return failure;
        if (Contract.String(body["profile_version"]) != Contract.String(candidate!["profile"]!["version"]))
            return ApiResult.Error("O perfil mudou. Reabra a candidatura e revise novamente.", 409);
        if (Contract.String(body["context_version"]) != Contract.String(candidate["context_version"]))
            return ApiResult.Error("O contexto ou currículo mudou. Reabra a candidatura e revise novamente.", 409);
        var selected = new JsonObject();
        foreach (var key in new[] { "version", "attempt_id", "digest", "profile_version", "context_version", "language" }) selected[key] = body[key]?.DeepClone();
        var payload = PyJson.Serialize(selected, sortKeys: true);
        try
        {
            using var connection = db.Open();
            using var tx = connection.BeginTransaction();
            EnsureTables(connection, tx);
            var previous = connection.QueryFirstOrDefault<AttemptRow>("SELECT job_id AS JobId, payload AS Payload FROM qpid_application_attempts WHERE attempt_id=@attempt", new { attempt }, tx);
            if (previous is not null)
            {
                if (previous.JobId != jobId || previous.Payload != payload) return ApiResult.Error("Reserva já utilizada com outros dados.", 409);
                return ApiResult.Ok(new JsonObject { ["ok"] = true, ["attempt_id"] = attempt, ["existing"] = true });
            }
            connection.Execute("INSERT INTO qpid_application_attempts(attempt_id,job_id,state,payload,created_at) VALUES(@attempt,@jobId,'submitting',@payload,@now)",
                new { attempt, jobId, payload, now = Clock.NowIso() }, tx);
            tx.Commit();
        }
        catch (Exception)
        {
            return ApiResult.Error("Já existe uma tentativa pendente para esta vaga ou a reserva não pôde ser salva. Confira antes de tentar novamente.", 409);
        }
        return ApiResult.Ok(new JsonObject { ["ok"] = true, ["attempt_id"] = attempt });
    }

    /// <summary>Divide a URL como o <c>urlsplit</c> do Python; só o que a validação do resultado precisa.</summary>
    internal static (string Scheme, string Netloc, string Path, string Host, string User, string Password)? SplitUrl(string raw)
    {
        var url = raw.TrimStart(Enumerable.Range(0, 33).Select(i => (char)i).ToArray()).Replace("\t", "").Replace("\r", "").Replace("\n", "");
        var scheme = "";
        var colon = url.IndexOf(':');
        if (colon > 0 && char.IsAsciiLetter(url[0]) && url[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            scheme = url[..colon].ToLowerInvariant();
            url = url[(colon + 1)..];
        }
        var netloc = "";
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            var end = url.IndexOfAny(['/', '?', '#'], 2);
            netloc = end < 0 ? url[2..] : url[2..end];
            url = end < 0 ? "" : url[end..];
            if ((netloc.Contains('[') && !netloc.Contains(']')) || (netloc.Contains(']') && !netloc.Contains('['))) return null; // "Invalid IPv6 URL"
        }
        var path = url;
        var cut = path.IndexOfAny(['#', '?']);
        if (cut >= 0) path = path[..cut];
        var at = netloc.LastIndexOf('@');
        var userinfo = at >= 0 ? netloc[..at] : "";
        var hostinfo = at >= 0 ? netloc[(at + 1)..] : netloc;
        string host;
        if (hostinfo.StartsWith('[')) host = hostinfo[1..Math.Max(1, hostinfo.IndexOf(']'))];
        else host = hostinfo.Split(':')[0];
        var colonUser = userinfo.IndexOf(':');
        var user = at < 0 ? "" : colonUser < 0 ? userinfo : userinfo[..colonUser];
        var password = at < 0 || colonUser < 0 ? "" : userinfo[(colonUser + 1)..];
        return (scheme, netloc, path, host.ToLowerInvariant(), user, password);
    }

    private static readonly string[] States = ["submitted", "submission-unknown", "failed-before-click"];
    private static readonly string[] Evidences = ["success-message-and-form-finished", "page-changed", "validation", "timeout", "interrupted"];

    public ApiResult Result(long jobId, JsonObject body)
    {
        var state = Contract.String(body["state"]);
        var attempt = Contract.String(body["attempt_id"]);
        var evidence = Contract.String(body["evidence"]);
        if (!Contract.EqualsOne(body["version"]) || state is null || !States.Contains(state) || attempt is null || !AttemptId().IsMatch(attempt) || evidence is null || !Evidences.Contains(evidence))
            return ApiResult.Error("Resultado inválido.");
        if (state == "submitted" && evidence != "success-message-and-form-finished") return ApiResult.Error("Envio sem evidência observada.");
        var parts = SplitUrl(Contract.String(body["url"]) ?? "");
        if (parts is not { } url || url.Scheme != "https" || url.Host.Length == 0 || url.User.Length > 0 || url.Password.Length > 0)
            return ApiResult.Error("Endereço de resultado inválido.");
        var hash = Contract.String(body["resume_hash"]);
        var safe = new JsonObject
        {
            ["version"] = 1, ["attempt_id"] = attempt, ["state"] = state, ["evidence"] = evidence, ["url"] = $"https://{url.Netloc}{url.Path}",
            ["resume_hash"] = hash is not null && Sha256Hex().IsMatch(hash) ? hash : null,
            ["automatic"] = body["automatic"] is JsonValue a && a.TryGetValue<bool>(out var automatic) && automatic,
            ["source"] = "qpid-observed",
        };
        var payload = PyJson.Serialize(safe, sortKeys: true);
        var now = Clock.NowIso();
        using (var connection = db.Open())
        using (var tx = connection.BeginTransaction())
        {
            EnsureTables(connection, tx);
            var reservation = connection.QueryFirstOrDefault<long?>("SELECT job_id FROM qpid_application_attempts WHERE attempt_id=@attempt", new { attempt }, tx);
            if (reservation != jobId) return ApiResult.Error("Não há reserva correspondente a este resultado.", 409);
            var previous = connection.QueryFirstOrDefault<string?>("SELECT payload FROM qpid_application_results WHERE attempt_id=@attempt", new { attempt }, tx);
            if (previous is not null)
                return previous == payload ? ApiResult.Ok(new JsonObject { ["ok"] = true, ["existing"] = true }) : ApiResult.Error("Resultado já registrado com outros dados.", 409);
            connection.Execute("INSERT INTO qpid_application_results(attempt_id,job_id,state,payload,created_at) VALUES(@attempt,@jobId,@state,@payload,@now)", new { attempt, jobId, state, payload, now }, tx);
            connection.Execute("UPDATE qpid_application_attempts SET state=@state WHERE attempt_id=@attempt", new { state, attempt }, tx);
            if (state == "submitted") connection.Execute("UPDATE jobs SET status='applied', applied_at=COALESCE(applied_at, @now) WHERE id=@jobId", new { now, jobId }, tx);
            tx.Commit();
        }
        if (state == "submitted") db.Bus.Publish(new JobsChanged([jobId]));
        return ApiResult.Ok(new JsonObject { ["ok"] = true, ["source"] = "qpid-observed", ["state"] = state });
    }

    public ApiResult Results(long jobId)
    {
        using var connection = db.Open();
        EnsureTables(connection);
        var payloads = connection.Query<string>("SELECT payload FROM qpid_application_results WHERE job_id=@jobId ORDER BY created_at DESC LIMIT 20", new { jobId });
        return ApiResult.Ok(new JsonObject { ["items"] = new JsonArray(payloads.Select(p => JsonNode.Parse(p)).ToArray()) });
    }

    public ApiResult Confirmed(long jobId, JsonObject body)
    {
        if (body["confirmed"] is not JsonValue v || !v.TryGetValue<bool>(out var confirmed) || !confirmed) return ApiResult.Error("Confirmação explícita obrigatória.");
        var now = Clock.NowIso();
        using (var connection = db.Open())
        using (var tx = connection.BeginTransaction())
        {
            if (connection.QueryFirstOrDefault<string?>("SELECT status FROM jobs WHERE id=@jobId", new { jobId }, tx) is null) return ApiResult.Error("Vaga não encontrada", 404);
            connection.Execute("UPDATE jobs SET status='applied', applied_at=COALESCE(applied_at, @now) WHERE id=@jobId", new { now, jobId }, tx);
            EnsureTables(connection, tx);
            var declared = new JsonObject { ["version"] = 1, ["attempt_id"] = $"manual-{jobId}", ["state"] = "user-declared", ["source"] = "user-declared", ["evidence"] = "user-confirmation" };
            connection.Execute("INSERT INTO qpid_application_results(attempt_id,job_id,state,payload,created_at) VALUES(@attempt,@jobId,@state,@payload,@now) ON CONFLICT(attempt_id) DO NOTHING",
                new { attempt = $"manual-{jobId}", jobId, state = "user-declared", payload = PyJson.Serialize(declared, sortKeys: true), now }, tx);
            tx.Commit();
        }
        db.Bus.Publish(new JobsChanged([jobId]));
        return ApiResult.Ok(new JsonObject { ["ok"] = true, ["source"] = "user-declared", ["notice"] = "Envio confirmado pelo usuário no Qpid." });
    }
}
