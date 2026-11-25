using System.Text;
using Dapper;
using Odradek.Core;
using Odradek.Core.Actions;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>Banco descartável com o esquema completo; nunca toca no jobs.db real nem no Cofre de Credenciais.</summary>
public sealed class TempDb : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odradek-tests-" + Guid.NewGuid().ToString("N"));
    public ChangeBus Bus { get; } = new();
    public Database Db { get; }
    public List<Change> Changes { get; } = [];

    public TempDb()
    {
        Directory.CreateDirectory(_dir);
        Db = new Database(Path.Combine(_dir, "jobs.db"), bus: Bus);
        Db.Initialize();
        Bus.Published += Changes.Add;
    }

    public long AddJob(string status = "worth", string location = "Remote - Brazil", string? posted = null, string seen = "2026-11-20T12:00:00+00:00", string description = "", string title = "Engineer", long? score = null)
    {
        using var c = Db.Open();
        var id = c.ExecuteScalar<long>(
            "INSERT INTO jobs(source,title,company,location,description,url,posted_at,first_seen_at,status,fingerprint) VALUES('t',@title,'Acme',@location,@description,'https://x.test/'||abs(random()),@posted,@seen,@status,hex(randomblob(8))) RETURNING id",
            new { title, location, description, posted, seen, status });
        if (score is not null)
            c.Execute("INSERT INTO ai_decisions(job_id,decided_at,match_score,should_apply,letter_required,reason) VALUES(@id,@now,@score,1,0,'r')", new { id, now = Clock.NowIso(), score });
        return id;
    }

    public T Scalar<T>(string sql, object? p = null) { using var c = Db.Open(); return c.ExecuteScalar<T>(sql, p)!; }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }
}

public class JobActionsTests
{
    [Fact]
    public void Status_changes_and_stamps_applied_at_only_when_applied()
    {
        using var t = new TempDb();
        var id = t.AddJob();
        var actions = new JobActions(t.Db);
        Assert.Equal("Status atualizado.", actions.SetStatus(id, "saved"));
        Assert.Equal("saved", t.Scalar<string>("SELECT status FROM jobs WHERE id=@id", new { id }));
        Assert.Null(t.Scalar<string?>("SELECT applied_at FROM jobs WHERE id=@id", new { id }));
        actions.SetStatus(id, "applied");
        var stamp = t.Scalar<string>("SELECT applied_at FROM jobs WHERE id=@id", new { id });
        Assert.EndsWith("+00:00", stamp);
        actions.SetStatus(id, "ignored"); // não apaga a data de aplicação (COALESCE)
        Assert.Equal(stamp, t.Scalar<string>("SELECT applied_at FROM jobs WHERE id=@id", new { id }));
        Assert.Contains(t.Changes, c => c is JobsChanged j && j.Ids.Contains(id));
    }

    [Fact]
    public void Status_rejects_unknown_status_and_missing_job()
    {
        using var t = new TempDb();
        var id = t.AddJob();
        var actions = new JobActions(t.Db);
        Assert.Equal("Status inválido", Assert.Throws<ActionException>(() => actions.SetStatus(id, "review")).Message);
        Assert.Equal("Vaga não encontrada", Assert.Throws<ActionException>(() => actions.SetStatus(999, "saved")).Message);
    }

    [Fact]
    public void Notes_are_saved_and_limited_to_30000_code_points()
    {
        using var t = new TempDb();
        var id = t.AddJob();
        var actions = new JobActions(t.Db);
        Assert.Equal("Anotação salva.", actions.SetNotes(id, "olá 😀"));
        Assert.Equal("olá 😀", t.Scalar<string>("SELECT notes FROM jobs WHERE id=@id", new { id }));
        actions.SetNotes(id, string.Concat(Enumerable.Repeat("😀", 30000))); // 30000 pontos de código = 60000 unidades UTF-16: ainda vale
        Assert.Throws<ActionException>(() => actions.SetNotes(id, new string('a', 30001)));
    }

    [Fact]
    public void BulkIgnore_requires_a_filter_and_validates_each_field()
    {
        var empty = BulkIgnoreCriteria.Empty;
        Assert.Equal("Informe ao menos um filtro ou selecione todas.", Assert.Throws<ActionException>(() => empty.Validated()).Message);
        Assert.Equal("Use 'todas' sem outros filtros.", Assert.Throws<ActionException>(() => (empty with { All = true, Country = "x" }).Validated()).Message);
        Assert.Equal("Use datas no formato AAAA-MM-DD.", Assert.Throws<ActionException>(() => (empty with { FoundFrom = "2026-13-01" }).Validated()).Message);
        Assert.Equal("A data inicial deve ser anterior à final.", Assert.Throws<ActionException>(() => (empty with { PostedFrom = "2026-02-02", PostedTo = "2026-01-01" }).Validated()).Message);
        Assert.Equal("O match deve estar entre 0 e 100.", Assert.Throws<ActionException>(() => (empty with { MatchMin = "101" }).Validated()).Message);
        Assert.Equal("O match mínimo deve ser menor ou igual ao máximo.", Assert.Throws<ActionException>(() => (empty with { MatchMin = "80", MatchMax = "70" }).Validated()).Message);
        Assert.Equal("O ID da vaga deve ser um número positivo.", Assert.Throws<ActionException>(() => (empty with { JobId = "0" }).Validated()).Message);
        (empty with { All = true }).Validated();
    }

    [Fact]
    public void BulkIgnore_previews_applies_and_refuses_a_stale_count()
    {
        using var t = new TempDb();
        var br1 = t.AddJob(location: "Sao Paulo, Brazil", score: 90);
        var br2 = t.AddJob(location: "Remote - BRAZIL", score: 40);
        var us = t.AddJob(location: "Austin, USA", score: 95);
        var saved = t.AddJob(status: "saved", location: "Brazil");
        var actions = new JobActions(t.Db);
        var brazil = BulkIgnoreCriteria.Empty with { Country = "brazil" };
        Assert.Equal(2, actions.BulkIgnorePreview(brazil)); // só "worth", sem diferenciar maiúsculas
        Assert.Equal(1, actions.BulkIgnorePreview(BulkIgnoreCriteria.Empty with { MatchMin = "50", Country = "brazil" }));
        Assert.Equal(1, actions.BulkIgnorePreview(BulkIgnoreCriteria.Empty with { MatchMax = "50" }));
        var stale = Assert.Throws<ActionException>(() => actions.BulkIgnoreApply(brazil, 3));
        Assert.Equal("A quantidade de vagas mudou. Confira novamente antes de ignorar.", stale.Message);
        Assert.Equal("Confira a quantidade antes de ignorar.", Assert.Throws<ActionException>(() => actions.BulkIgnoreApply(brazil, 0)).Message);
        Assert.Equal("2 vaga(s) ignorada(s).", actions.BulkIgnoreApply(brazil, 2));
        Assert.Equal("ignored", t.Scalar<string>("SELECT status FROM jobs WHERE id=@br1", new { br1 }));
        Assert.Equal("ignored", t.Scalar<string>("SELECT status FROM jobs WHERE id=@br2", new { br2 }));
        Assert.Equal("worth", t.Scalar<string>("SELECT status FROM jobs WHERE id=@us", new { us }));
        Assert.Equal("saved", t.Scalar<string>("SELECT status FROM jobs WHERE id=@saved", new { saved }));
    }

    [Fact]
    public void BulkIgnore_treats_like_wildcards_in_country_literally()
    {
        using var t = new TempDb();
        t.AddJob(location: "100% Remote");
        t.AddJob(location: "Remote");
        Assert.Equal(1, new JobActions(t.Db).BulkIgnorePreview(BulkIgnoreCriteria.Empty with { Country = "100%" }));
        Assert.Equal(0, new JobActions(t.Db).BulkIgnorePreview(BulkIgnoreCriteria.Empty with { Country = "_emote_" }));
    }

    [Fact]
    public void BulkIgnore_date_filters_use_brasilia_days()
    {
        using var t = new TempDb();
        t.AddJob(seen: "2026-11-20T02:30:00+00:00"); // 19/11 23:30 em Brasília
        t.AddJob(seen: "2026-11-20T12:00:00+00:00");
        var actions = new JobActions(t.Db);
        Assert.Equal(1, actions.BulkIgnorePreview(BulkIgnoreCriteria.Empty with { FoundFrom = "2026-11-20", FoundTo = "2026-11-20" }));
        Assert.Equal(2, actions.BulkIgnorePreview(BulkIgnoreCriteria.Empty with { FoundFrom = "2026-11-19", FoundTo = "2026-11-20" }));
    }

    [Fact]
    public void PurgeIgnored_empties_content_keeps_the_row_and_spares_unreviewed_ai_discards()
    {
        using var t = new TempDb();
        long Decision(long job, int shouldApply)
        {
            using var c = t.Db.Open();
            c.Execute("INSERT INTO ai_decisions(job_id,decided_at,match_score,should_apply,letter_required,reason,assessment_json) VALUES(@job,@now,60,@shouldApply,0,'motivo','{\"a\":1}')",
                new { job, now = Clock.NowIso(), shouldApply });
            c.Execute("INSERT INTO cover_letters(job_id,language,provider,model,body,created_at,updated_at) VALUES(@job,'pt','p','m','carta',@now,@now)", new { job, now = Clock.NowIso() });
            return job;
        }
        var byHand = Decision(t.AddJob(status: "ignored", description: "texto longo", title: "Manual"), 1); // ignorada à mão (decisão aprovava)
        var discarded = Decision(t.AddJob(status: "ignored", description: "texto da IA"), 0);              // descartada pela IA, não conferida
        var reviewed = Decision(t.AddJob(status: "ignored", description: "texto conferido"), 0);
        var worth = Decision(t.AddJob(description: "vale"), 1);
        t.Scalar<int>("UPDATE jobs SET reviewed_at='2026-11-21T00:00:00+00:00' WHERE id=@reviewed RETURNING 1", new { reviewed });
        var actions = new JobActions(t.Db);

        Assert.Equal(2, actions.PurgeIgnoredPreview());
        Assert.Equal("2 vaga(s) ignorada(s) limpa(s) e banco compactado.", actions.PurgeIgnored());
        foreach (var id in new[] { byHand, reviewed })
        {
            Assert.Equal("", t.Scalar<string>("SELECT description FROM jobs WHERE id=@id", new { id }));
            Assert.Equal(0, t.Scalar<int>("SELECT COUNT(*) FROM cover_letters WHERE job_id=@id", new { id }));
            Assert.Equal("", t.Scalar<string>("SELECT assessment_json FROM ai_decisions WHERE job_id=@id", new { id }));
            Assert.Equal("motivo", t.Scalar<string>("SELECT reason FROM ai_decisions WHERE job_id=@id", new { id })); // nota e motivo ficam
            Assert.Equal("ignored", t.Scalar<string>("SELECT status FROM jobs WHERE id=@id", new { id }));          // a linha fica: o fingerprint segue ocupado
        }
        foreach (var id in new[] { discarded, worth }) // fora do escopo
        {
            Assert.NotEqual("", t.Scalar<string>("SELECT description FROM jobs WHERE id=@id", new { id }));
            Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM cover_letters WHERE job_id=@id", new { id }));
        }
        Assert.Contains(t.Changes, c => c is JobsChanged);
        Assert.Equal(0, actions.PurgeIgnoredPreview());
        Assert.Equal("Nenhuma vaga ignorada para limpar.", actions.PurgeIgnored()); // idempotente
    }

    [Fact]
    public void Storage_reports_the_database_size_and_compacting_returns_the_free_space()
    {
        using var t = new TempDb();
        var big = new string('x', 20_000);
        for (var i = 0; i < 60; i++) t.AddJob(description: big);
        t.AddJob(description: "");
        var before = UsageReader.Read(t.Db).Storage!;
        Assert.Equal((61L, 60L, 0L), (before.Jobs, before.JobsWithDescription, before.CoverLetters));
        Assert.True(before.TotalBytes > 1_000_000);

        using (var c = t.Db.Open()) c.Execute("DELETE FROM jobs WHERE description <> ''");
        var deleted = UsageReader.Storage(t.Db)!;
        Assert.True(deleted.FreeBytes > 1_000_000, $"livres: {deleted.FreeBytes}"); // apagar não devolve o espaço sozinho

        Assert.Equal("Banco compactado.", new JobActions(t.Db).Compact());
        var after = UsageReader.Storage(t.Db)!;
        Assert.Equal(0, after.FreeBytes);
        Assert.Equal(new FileInfo(t.Db.Path).Length, after.DbBytes);
        Assert.True(after.TotalBytes < deleted.TotalBytes - 1_000_000, $"antes {deleted.TotalBytes}, depois {after.TotalBytes}");
    }

    [Fact]
    public void MarkReviewed_only_touches_ignored_unreviewed_jobs_and_limits_ids()
    {
        using var t = new TempDb();
        var ignored = t.AddJob(status: "ignored");
        var worth = t.AddJob();
        var actions = new JobActions(t.Db);
        Assert.Equal("1 vaga(s) marcada(s) como conferida(s).", actions.MarkReviewed([ignored, worth]));
        Assert.Equal("0 vaga(s) marcada(s) como conferida(s).", actions.MarkReviewed([ignored])); // já conferida
        Assert.Throws<ActionException>(() => actions.MarkReviewed([]));
        Assert.Throws<ActionException>(() => actions.MarkReviewed(Enumerable.Range(1, 101).Select(i => (long)i).ToList()));
    }
}

public class QueueActionsTests
{
    private static readonly KeyValuePair<string, object?>[] Payload = [new("job_id", 7L)];

    [Fact]
    public void The_menu_badge_counts_only_work_that_is_waiting_or_running()
    {
        using var t = new TempDb();
        var queue = new QueueActions(t.Db);
        var counts = new PanelQueries(t.Db);
        Assert.Equal(0, counts.QueueActiveCount());
        var ids = Enumerable.Range(1, 5).Select(i => queue.Enqueue("job_apply", [new("job_id", (long)i)])).ToList();
        Assert.Equal(5, counts.QueueActiveCount()); // todos pendentes
        using (var c = t.Db.Open())
        {
            c.Execute("UPDATE queue_jobs SET status='running' WHERE id=@id", new { id = ids[0] });
            c.Execute("UPDATE queue_jobs SET status='retry_wait' WHERE id=@id", new { id = ids[1] });
            c.Execute("UPDATE queue_jobs SET status='succeeded' WHERE id=@id", new { id = ids[2] });
            c.Execute("UPDATE queue_jobs SET status='failed' WHERE id=@id", new { id = ids[3] });
        }
        Assert.Equal(3, counts.QueueActiveCount()); // rodando + esperando nova tentativa + o pendente; concluído e falho não contam
        Assert.True(queue.Cancel(ids[4]));
        Assert.Equal(2, counts.QueueActiveCount());
    }

    [Fact]
    public void Enqueue_writes_a_pending_item_with_python_style_json_and_limits_from_settings()
    {
        using var t = new TempDb();
        new SettingsStore(t.Db).SetMany(new Dictionary<string, string> { ["queue_max_attempts"] = "5", ["queue_ttl_hours"] = "2" });
        var id = new QueueActions(t.Db).Enqueue("job_apply", Payload, "apply:7");
        Assert.Equal("{\"job_id\": 7, \"_dedupe\": \"apply:7\"}", t.Scalar<string>("SELECT payload FROM queue_jobs WHERE id=@id", new { id }));
        Assert.Equal("pending", t.Scalar<string>("SELECT status FROM queue_jobs WHERE id=@id", new { id }));
        Assert.Equal(5, t.Scalar<int>("SELECT max_attempts FROM queue_jobs WHERE id=@id", new { id }));
        var next = DateTime.Parse(t.Scalar<string>("SELECT expires_at FROM queue_jobs WHERE id=@id", new { id }), null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange((next - DateTime.UtcNow).TotalHours, 1.9, 2.1);
        Assert.Contains(t.Changes, c => c is QueueChanged);
    }

    [Fact]
    public void Enqueue_reuses_an_active_item_with_the_same_dedupe_key_but_not_a_finished_one()
    {
        using var t = new TempDb();
        var queue = new QueueActions(t.Db);
        var first = queue.Enqueue("job_apply", Payload, "apply:7");
        Assert.Equal(first, queue.Enqueue("job_apply", Payload, "apply:7"));
        Assert.NotEqual(first, queue.Enqueue("job_apply", [new("job_id", 8L)], "apply:8"));
        Assert.True(queue.Cancel(first));
        Assert.NotEqual(first, queue.Enqueue("job_apply", Payload, "apply:7"));
        Assert.Throws<ActionException>(() => queue.Enqueue("nope", Payload));
    }

    [Fact]
    public void Cancel_retry_and_clear_respect_the_state_machine()
    {
        using var t = new TempDb();
        var queue = new QueueActions(t.Db);
        var a = queue.Enqueue("job_apply", [new("job_id", 1L)]);
        Assert.False(queue.RetryNow(a)); // pendente não é reenfileirável
        Assert.True(queue.Cancel(a));
        Assert.False(queue.Cancel(a)); // já cancelado
        Assert.Equal("Cancelado manualmente", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@a", new { a }));
        Assert.True(queue.RetryNow(a));
        Assert.Equal("pending", t.Scalar<string>("SELECT status FROM queue_jobs WHERE id=@a", new { a }));
        Assert.Equal("", t.Scalar<string>("SELECT last_error FROM queue_jobs WHERE id=@a", new { a }));

        var b = queue.Enqueue("job_apply", [new("job_id", 2L)]);
        using (var c = t.Db.Open()) c.Execute("UPDATE queue_jobs SET status='failed', attempts=9 WHERE id=@b", new { b });
        Assert.Equal(1, queue.RetryAll());
        Assert.Equal(0, t.Scalar<int>("SELECT attempts FROM queue_jobs WHERE id=@b", new { b }));

        using (var c = t.Db.Open()) c.Execute("UPDATE queue_jobs SET status='succeeded' WHERE id=@a", new { a });
        Assert.Equal(1, queue.ClearTerminal());
        Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs"));
    }

    [Fact]
    public void RetryAll_never_touches_the_legacy_linkedin_apply_kind()
    {
        using var t = new TempDb();
        using (var c = t.Db.Open())
            c.Execute("INSERT INTO queue_jobs(kind,payload,status,next_run_at,expires_at,created_at,updated_at) VALUES('linkedin_apply','{}','failed','x','x','x','x')");
        var queue = new QueueActions(t.Db);
        Assert.Equal(0, queue.RetryAll());
        Assert.False(queue.RetryNow(1));
    }

    [Fact]
    public void EvaluateNew_queues_new_jobs_preferred_country_first_and_moves_them_to_review()
    {
        using var t = new TempDb();
        var recentUs = t.AddJob(status: "new", location: "Austin, United States", posted: "1 day ago");
        var oldBr = t.AddJob(status: "new", location: "São Paulo, Brasil", posted: "5 days ago");
        var unknownBr = t.AddJob(status: "new", location: "Remote Brazil");
        var worth = t.AddJob(status: "worth");
        new SettingsStore(t.Db).Set("linkedin_preferred_countries", "Brazil");
        var note = new QueueActions(t.Db).EvaluateNew();
        Assert.Equal("3 vaga(s) enfileirada(s) para triagem (até 3 worker(s) em paralelo)", note);
        var order = new List<long>();
        using (var c = t.Db.Open())
            order.AddRange(c.Query<string>("SELECT payload FROM queue_jobs ORDER BY id").Select(p => long.Parse(System.Text.RegularExpressions.Regex.Match(p, "\"job_id\": (\\d+)").Groups[1].Value)));
        Assert.Equal([oldBr, unknownBr, recentUs], order); // Brasil primeiro (mais recente antes; sem data por último), depois o resto
        Assert.Equal(3, t.Scalar<int>("SELECT COUNT(*) FROM jobs WHERE status='review'"));
        Assert.Equal("worth", t.Scalar<string>("SELECT status FROM jobs WHERE id=@worth", new { worth }));
        Assert.Equal("nenhuma vaga nova para enfileirar", new QueueActions(t.Db).EvaluateNew());
    }

    [Fact]
    public void EvaluateNew_with_a_limit_leaves_the_rest_for_later()
    {
        using var t = new TempDb();
        for (var i = 0; i < 3; i++) t.AddJob(status: "new");
        var note = new QueueActions(t.Db).EvaluateNew(2);
        Assert.Contains("2 vaga(s) enfileirada(s)", note);
        Assert.Contains("limite atingido, 1 vaga(s) nova(s) ficaram para a próxima execução", note);
        Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM jobs WHERE status='new'"));
    }
}

public class SettingsActionsTests
{
    private static string Msg(SettingsActions a, params (string, string)[] pairs) =>
        Assert.Throws<ActionException>(() => a.Save(pairs.ToDictionary(p => p.Item1, p => p.Item2))).Message;

    [Fact]
    public void Search_group_validations_use_the_python_messages()
    {
        using var t = new TempDb();
        var a = new SettingsActions(t.Db);
        Assert.Equal("Máximo de vagas Apify deve ficar entre 1 e 100", Msg(a, ("apify_job_count", "101")));
        Assert.Equal("Limite Apify inválido", Msg(a, ("apify_monthly_credit_limit_usd", "-1")));
        Assert.Equal("Limite Apify inválido", Msg(a, ("apify_monthly_credit_limit_usd", "abc")));
        Assert.Equal("A fatia do filtro Principal deve ficar entre 50 e 95", Msg(a, ("linkedin_main_share", "49")));
        Assert.Equal("A idade máxima das vagas deve ficar entre 1 e 30 dias", Msg(a, ("linkedin_max_age_days", "31")));
        Assert.Equal("Informe no máximo 5 países com viés", Msg(a, ("linkedin_preferred_countries", "A, B, C, D, E, F")));
        Assert.StartsWith("Países com viés:", Msg(a, ("linkedin_preferred_countries", "Brazil 123")));
        Assert.StartsWith("JSON de actors Apify inválido", Msg(a, ("apify_actors_json", "{")));
        Assert.Equal("apify_actors_json deve ser uma lista JSON com pelo menos um actor.", Msg(a, ("apify_actors_json", "[]")));
        Assert.Equal("Actor Apify #1 sem id.", Msg(a, ("apify_actors_json", "[{}]")));
        Assert.Equal("Valor inválido para keywords", Msg(a, ("keywords", new string('x', 30001))));
    }

    [Fact]
    public void Search_group_validates_saved_filters_and_does_not_store_the_default_prompts()
    {
        using var t = new TempDb();
        var a = new SettingsActions(t.Db);
        Assert.Equal("Use um único filtro (objeto), não uma lista.", Msg(a, ("linkedin_main_filter_json", "[]")));
        Assert.Equal("Dê um nome ao filtro.", Msg(a, ("linkedin_main_filter_json", """{"keywords":["a"]}""")));
        Assert.Equal("Adicione ao menos uma keyword ao filtro.", Msg(a, ("linkedin_secondary_filter_json", """{"name":"n","keywords":[]}""")));
        Assert.StartsWith("Use no máximo 2 frases por filtro", Msg(a, ("linkedin_main_filter_json", """{"name":"n","keywords":["a","b","c"]}""")));

        a.Save(new Dictionary<string, string>
        {
            ["linkedin_main_filter_json"] = """{"name":"Dev","keywords":["Backend Engineer"],"experience_levels":[3]}""",
            ["linkedin_filter_prompt"] = "  " + Odradek.Core.Collector.FilterPrompts.DefaultMain + "   ",
            ["transferable_prompt"] = "meu prompt",
        });
        var store = new Odradek.Core.Data.SettingsStore(t.Db);
        Assert.StartsWith("""{"id": "main-""", store.Get("linkedin_main_filter_json"));
        Assert.Equal("", store.Get("linkedin_filter_prompt"));
        Assert.Equal("meu prompt", store.Get("transferable_prompt"));
        a.Save(new Dictionary<string, string> { ["linkedin_main_filter_json"] = "" });
        Assert.Equal("", store.Get("linkedin_main_filter_json")); // vazio = sem filtro
    }

    [Fact]
    public void The_selection_prompt_equal_to_the_default_is_not_stored_but_a_custom_one_is()
    {
        using var t = new TempDb();
        var a = new SettingsActions(t.Db);
        var store = new Odradek.Core.Data.SettingsStore(t.Db);
        a.Save(new Dictionary<string, string> { ["job_selection_prompt"] = Odradek.Core.Engines.TriageService.DefaultJobSelectionPrompt + "   " });
        Assert.Equal("", store.Get("job_selection_prompt"));
        a.Save(new Dictionary<string, string> { ["job_selection_prompt"] = "Seja rigoroso com senioridade." });
        Assert.Equal("Seja rigoroso com senioridade.", store.Get("job_selection_prompt"));
        Assert.Equal("Seja rigoroso com senioridade.", Odradek.Core.Engines.TriageService.EffectivePrompt(store.All()));
        a.Save(new Dictionary<string, string> { ["job_selection_prompt"] = "" });
        Assert.Equal(Odradek.Core.Engines.TriageService.DefaultJobSelectionPrompt, Odradek.Core.Engines.TriageService.EffectivePrompt(store.All()));
    }

    [Fact]
    public void Search_group_normalizes_countries_toggles_and_saves_only_known_keys()
    {
        using var t = new TempDb();
        var a = new SettingsActions(t.Db);
        var notice = a.Save(new Dictionary<string, string>
        {
            ["linkedin_preferred_countries"] = "brazil ( Belo Horizonte );Portugal\nbrazil", ["geo_eligibility_enabled"] = "yes", ["keywords"] = "python",
            ["bogus_key"] = "x",
        });
        Assert.Equal("Preferências de busca salvas.", notice);
        var store = new SettingsStore(t.Db);
        Assert.Equal("brazil (Belo Horizonte), Portugal", store.Get("linkedin_preferred_countries"));
        Assert.Equal("0", store.Get("geo_eligibility_enabled"));
        Assert.Equal("python", store.Get("keywords"));
        Assert.Equal("", store.Get("bogus_key"));
        Assert.Contains(t.Changes, c => c is SettingsChanged);
    }

    [Fact]
    public void Automation_ai_smtp_and_profile_validations()
    {
        using var t = new TempDb();
        var a = new SettingsActions(t.Db);
        Assert.Equal("Valor inválido para queue_max_workers: use 1 a 8", Msg(a, ("queue_max_workers", "9")));
        Assert.Equal("Valor inválido para minimum_match_score: use 0 a 100", Msg(a, ("minimum_match_score", "-5")));
        Assert.Equal("Provedor de IA inválido", Msg(a, ("ai_provider", "claude")));
        Assert.StartsWith("Modelos do TokenHarbor:", Msg(a, ("tokenharbor_fallback_models", "a, b, c, d, e, f, g")));
        Assert.StartsWith("Modelos do TokenHarbor:", Msg(a, ("tokenharbor_fallback_models", "bad model!")));
        Assert.Equal("Porta SMTP inválida", Msg(a, ("smtp_port", "70000")));
        Assert.StartsWith("URL do portfólio inválida", Msg(a, ("portfolio_profile_url", "ftp://x")));
        Assert.StartsWith("URL de backup inválida", Msg(a, ("backup_database_url", "mysql://x")));
        Assert.Equal("Valor inválido para api_key", Msg(a, ("api_key", new string('k', 8193))));
    }

    [Fact]
    public void Ai_group_normalizes_models_optimism_and_auto_apply()
    {
        using var t = new TempDb();
        var a = new SettingsActions(t.Db);
        Assert.Equal("IA e integrações salvas.", a.Save(new Dictionary<string, string>
            { ["tokenharbor_fallback_models"] = "m1:free,\n m2:free, m1:free", ["job_selection_optimism"] = "250", ["ai_provider"] = "openai" }));
        var store = new SettingsStore(t.Db);
        Assert.Equal("m1:free, m2:free", store.Get("tokenharbor_fallback_models"));
        Assert.Equal("100", store.Get("job_selection_optimism"));
        Assert.Equal("Automação salva.", a.Save(new Dictionary<string, string> { ["auto_apply"] = "true", ["queue_max_workers"] = "4" }));
        Assert.Equal("0", store.Get("auto_apply"));
        Assert.Equal("4", store.Get("queue_max_workers"));
        Assert.Equal(75, SettingsActions.NormalizeOptimism(""));
        Assert.Equal(75, SettingsActions.NormalizeOptimism("abc"));
        Assert.Equal(0, SettingsActions.NormalizeOptimism("-3"));
        Assert.Equal(42, SettingsActions.NormalizeOptimism("42.9"));
    }

    [Fact]
    public void Validation_failure_saves_nothing_from_other_groups()
    {
        using var t = new TempDb();
        var a = new SettingsActions(t.Db);
        Assert.Throws<ActionException>(() => a.Save(new Dictionary<string, string> { ["candidate_name"] = "Ana", ["smtp_port"] = "0" }));
        Assert.Equal("", new SettingsStore(t.Db).Get("candidate_name"));
    }
}

public class ExtensionTokenTests
{
    [Fact]
    public void ExtensionToken_is_shown_once_stored_as_a_hash_and_verified()
    {
        using var t = new TempDb();
        var actions = new ExtensionTokenActions(t.Db);
        Assert.False(actions.Status().Configured);
        var token = actions.Issue();
        Assert.StartsWith("odk_", token);
        Assert.Equal(47, token.Length); // "odk_" + 43
        Assert.DoesNotContain(token, new SettingsStore(t.Db).All().Values);
        Assert.True(actions.Status().Configured);
        Assert.NotNull(actions.Status().CreatedAt);
        Assert.True(actions.Verify(token));
        Assert.False(actions.Verify(token + "x"));
        Assert.False(actions.Verify(""));
        Assert.NotEqual(token, actions.Issue());
        Assert.False(actions.Verify(token)); // o antigo deixou de valer
        actions.Revoke();
        Assert.False(actions.Status().Configured);
    }

    [Fact]
    public void ExtensionToken_hash_is_sha256_hex_like_python()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ExtensionTokenActions.Digest("abc"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ExtensionTokenActions.Digest(""));
    }
}

public class ResumeActionsTests
{
    [Fact]
    public void CleanPages_removes_repeated_headers_and_joins_hyphenated_words()
    {
        var pages = Enumerable.Range(1, 4).Select(i => $"Cabeçalho\ntexto único {i} da pági-\nna seguinte {i}\nRodapé").ToList();
        var text = ResumeActions.CleanPages(pages);
        Assert.DoesNotContain("Cabeçalho", text);
        Assert.DoesNotContain("Rodapé", text);
        Assert.Contains("texto único 1 da página seguinte 1", text); // hifenização desfeita: "pági-" + "na seguinte"
        Assert.DoesNotContain("pági-", text);
        Assert.Equal(1, ResumeActions.CleanPages(["a\n\n\n\nb"]).Split('\n').Count(l => l.Length == 0)); // várias linhas em branco viram uma
    }

    [Fact]
    public void Upload_validates_language_pdf_header_and_size_before_touching_the_database()
    {
        using var t = new TempDb();
        var actions = new ResumeActions(t.Db);
        Assert.Equal("Idioma do currículo deve ser pt ou en.", Assert.Throws<ActionException>(() => actions.Upload("fr", "a.pdf", "%PDF-1.4"u8.ToArray(), false)).Message);
        Assert.Equal("Envie um arquivo PDF válido.", Assert.Throws<ActionException>(() => actions.Upload("pt", "a.pdf", "hello"u8.ToArray(), false)).Message);
        Assert.Equal("PDF maior que 12 MB.", Assert.Throws<ActionException>(() => actions.Upload("pt", "a.pdf", new byte[ResumeActions.MaxPdfBytes + 1].Select((_, i) => i < 4 ? "%PDF"u8[i] : (byte)0).ToArray(), false)).Message);
        Assert.Equal(0, t.Scalar<int>("SELECT COUNT(*) FROM resumes"));
    }

    [Fact]
    public void Upload_of_a_real_pdf_stores_it_enqueues_analysis_and_reuses_identical_analyzed_files()
    {
        using var t = new TempDb();
        var pdf = LetterExport.Pdf(string.Join("\n", Enumerable.Repeat("Experiência profissional em desenvolvimento de software e integrações de sistemas.", 6)));
        var actions = new ResumeActions(t.Db);
        var message = actions.Upload("pt", "cv.pdf", pdf, false);
        Assert.Contains("PDF salvo", message);
        Assert.Matches(@"Job de fila #\d+\.$", message);
        Assert.Equal("pending", t.Scalar<string>("SELECT analysis_status FROM resumes WHERE language='pt'"));
        Assert.Contains("Experiência profissional", t.Scalar<string>("SELECT extracted_text FROM resumes WHERE language='pt'"));
        Assert.Equal(pdf.Length, t.Scalar<int>("SELECT length(content) FROM resume_files"));
        Assert.Equal("{\"language\": \"pt\", \"force\": true, \"requested_at\": \"" + t.Scalar<string>("SELECT json_extract(payload,'$.requested_at') FROM queue_jobs") + "\", \"_dedupe\": \"resume:pt\"}",
            t.Scalar<string>("SELECT payload FROM queue_jobs"));
        // já analisado + mesmo PDF = reutiliza; com force = nova análise
        using (var c = t.Db.Open()) c.Execute("UPDATE resumes SET analysis_summary='resumo', analysis_status='ok'");
        Assert.Contains("PDF idêntico ao já analisado", actions.Upload("pt", "cv2.pdf", pdf, false));
        Assert.Equal("reused", t.Scalar<string>("SELECT analysis_status FROM resumes"));
        Assert.Equal("resumo", t.Scalar<string>("SELECT analysis_summary FROM resumes")); // a análise anterior fica
        Assert.Contains("mesmo PDF, mas reanálise forçada", actions.Upload("pt", "cv.pdf", pdf, true));
        Assert.Contains(t.Changes, c => c is ResumesChanged);
    }

    [Fact]
    public void Reanalyze_cancels_active_jobs_and_enqueues_a_new_forced_one()
    {
        using var t = new TempDb();
        var pdf = LetterExport.Pdf(string.Join("\n", Enumerable.Repeat("Experiência profissional em desenvolvimento de software e integrações de sistemas.", 6)));
        var actions = new ResumeActions(t.Db);
        Assert.Equal("Não há currículo PT salvo.", Assert.Throws<ActionException>(() => actions.Reanalyze("pt")).Message);
        Assert.Equal("Idioma inválido para reanálise.", Assert.Throws<ActionException>(() => actions.Reanalyze("xx")).Message);
        actions.Upload("pt", "cv.pdf", pdf, false);
        var message = actions.Reanalyze("pt");
        Assert.StartsWith("Reanálise do currículo PT enfileirada (job #2)", message);
        Assert.Equal("cancelled", t.Scalar<string>("SELECT status FROM queue_jobs WHERE id=1"));
        Assert.Equal("pending", t.Scalar<string>("SELECT status FROM queue_jobs WHERE id=2"));
        Assert.Equal("pending", t.Scalar<string>("SELECT analysis_status FROM resumes WHERE language='pt'"));
    }
}

public class SmtpAndLetterTests
{
    [Fact]
    public void Smtp_config_requires_host_sender_and_password_like_python()
    {
        var cfg = new Dictionary<string, string> { ["smtp_host"] = "", ["smtp_port"] = "587" };
        Assert.Equal("Configure o host SMTP no painel.", Assert.Throws<ActionException>(() => Smtp.ConfigFrom(cfg, "")).Message);
        cfg["smtp_host"] = "smtp.test";
        Assert.Equal("Configure o e-mail remetente (SMTP from / candidate_email).", Assert.Throws<ActionException>(() => Smtp.ConfigFrom(cfg, "")).Message);
        cfg["smtp_user"] = "me@test";
        Assert.Equal("Configure a senha SMTP no painel.", Assert.Throws<ActionException>(() => Smtp.ConfigFrom(cfg, "")).Message);
        var ok = Smtp.ConfigFrom(cfg, "pw");
        Assert.Equal("me@test", ok.From); // sem smtp_from nem candidate_email cai no usuário
        Assert.True(ok.UseTls);
        cfg["smtp_port"] = "x";
        Assert.Equal("Porta SMTP inválida.", Assert.Throws<ActionException>(() => Smtp.ConfigFrom(cfg, "pw")).Message);
    }

    [Fact]
    public void Smtp_message_is_utf8_base64_multipart_with_pdf_attachment()
    {
        var config = new SmtpConfig("smtp.test", 587, "u", "me@test", true, "p");
        var raw = Smtp.BuildMessage(config, ["a@x.com", "b@y.com"], "Candidatura: Engenheiro — Ana", "Olá, mundo ç", "%PDF-1.4 x"u8.ToArray(), "cv.pdf");
        Assert.Contains("To: a@x.com, b@y.com", raw);
        Assert.Contains("Subject: =?utf-8?B?", raw); // assunto com acento/travessão vai codificado
        Assert.Contains("multipart/mixed", raw);
        Assert.Contains("filename=\"cv.pdf\"", raw);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Olá, mundo ç")), raw.Replace("\r\n", ""));
        Assert.DoesNotContain("Olá", raw); // nada fora de ASCII no fio
        Assert.All(raw.Split("\r\n"), line => Assert.True(line.Length <= 998));
    }

    [Fact]
    public void Letter_pdf_is_a_valid_multi_page_document_with_a_correct_xref()
    {
        var body = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Prezados {i}, tenho grande interesse na vaga e experiência com sistemas, integrações, automação e atendimento (ao cliente) \\ ok."));
        var pdf = LetterExport.Pdf(body);
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        var pages = System.Text.RegularExpressions.Regex.Matches(text, "/Type /Page /Parent").Count;
        Assert.True(pages >= 2);
        Assert.Contains($"/Count {pages}", text);
        // cada entrada do xref aponta exatamente para "N 0 obj"
        var xrefAt = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"startxref\n(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", text[xrefAt..]);
        var entries = System.Text.RegularExpressions.Regex.Matches(text[xrefAt..], @"(\d{10}) 00000 n");
        for (var i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text[int.Parse(entries[i].Groups[1].Value)..]);
        // PdfPig (já aprovado) consegue ler e achar o texto
        var extracted = ResumeActions.ExtractText(pdf);
        Assert.Contains("Prezados", extracted);
        Assert.Contains("Prezados 40", extracted);
    }

    [Fact]
    public void Letter_pdf_keeps_portuguese_accents_and_maps_unsupported_characters()
    {
        var pdf = LetterExport.Pdf("Atenção: não é possível — “aspas” 😀 ção");
        var extracted = ResumeActions.ExtractText(LetterExport.Pdf(string.Join("\n", Enumerable.Repeat("Atenção: não é possível — “aspas” ção coração informações", 5))));
        Assert.Contains("Atenção", extracted);
        Assert.Contains("informações", extracted);
        Assert.True(pdf.Length > 200);
    }

    [Fact]
    public void Letter_txt_is_utf8_without_bom()
    {
        var bytes = LetterExport.Txt("olá");
        Assert.Equal(4, bytes.Length); // o, l, á (2 bytes)
        Assert.NotEqual(0xEF, bytes[0]);
    }
}

public class HelpersTests
{
    [Fact]
    public void PyJson_matches_python_json_dumps_without_ascii_escaping()
    {
        Assert.Equal("{\"a\": 1, \"b\": true, \"c\": \"ç\\n\\\"q\\\"\", \"d\": null}",
            PyJson.Dumps([new("a", 1), new("b", true), new("c", "ç\n\"q\""), new("d", null)]));
    }

    [Theory]
    [InlineData("Brazil (Belo Horizonte), Portugal", "Brazil|Belo Horizonte;Portugal|")]
    [InlineData("a;b\nc, A", "a|;b|;c|")]
    [InlineData("  ,  ", "")]
    public void ParsePreferred_splits_names_cities_and_dedupes(string raw, string expected) =>
        Assert.Equal(expected, string.Join(';', Geo.ParsePreferred(raw).Select(p => $"{p.Name}|{p.City}")));

    [Fact]
    public void PostedAgeDays_understands_iso_and_linkedin_relative_text()
    {
        Assert.Equal(3, Geo.PostedAgeDays("3 days ago"));
        Assert.Equal(14, Geo.PostedAgeDays("2 weeks ago"));
        Assert.Equal(1, Geo.PostedAgeDays("24 hours ago"));
        Assert.Equal(60, Geo.PostedAgeDays("há 2 meses"));
        Assert.Equal(0, Geo.PostedAgeDays("Just now"));
        Assert.Equal(5, Geo.PostedAgeDays("5d"));
        Assert.Null(Geo.PostedAgeDays("whenever"));
        Assert.Null(Geo.PostedAgeDays(""));
        Assert.InRange(Geo.PostedAgeDays(Clock.ToIso(DateTime.UtcNow.AddDays(-2)))!.Value, 1.99, 2.01);
    }

    [Fact]
    public void LocationInCountries_uses_synonyms_in_both_languages()
    {
        Assert.True(Geo.LocationInCountries("São Paulo, Brasil", ["Brazil"]));
        Assert.True(Geo.LocationInCountries("London, UK", ["United Kingdom"]));
        Assert.False(Geo.LocationInCountries("Brazilian Street, Austin", ["Brazil"])); // palavra inteira
    }
}
