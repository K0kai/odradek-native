using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>Servidor HTTP falso por rota: cada rota responde em sequência e repete a última resposta.</summary>
public sealed class RouteHandler : HttpMessageHandler
{
    public sealed record Seen(string Method, string Url, string? Body, string? Bearer, string? UserAgent);

    private readonly List<(string Method, string Contains, List<(int Status, string Body)> Replies, int[] Index)> _routes = [];
    public List<Seen> Requests { get; } = [];

    public RouteHandler On(string method, string contains, params string[] bodies) => OnStatus(method, contains, bodies.Select(b => (200, b)).ToArray());

    public RouteHandler OnStatus(string method, string contains, params (int Status, string Body)[] replies)
    {
        _routes.Add((method, contains, [.. replies], [0]));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Seen(request.Method.Method, url, body, request.Headers.Authorization?.Parameter, request.Headers.UserAgent.ToString()));
        foreach (var route in _routes)
        {
            if (route.Method != request.Method.Method || !url.Contains(route.Contains, StringComparison.Ordinal)) continue;
            var (status, text) = route.Replies[Math.Min(route.Index[0]++, route.Replies.Count - 1)];
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
        throw new InvalidOperationException($"rota não prevista: {request.Method} {url}");
    }
}

public class JobCollectorTests
{
    private static async IAsyncEnumerable<CollectedJob> Jobs(params CollectedJob[] jobs)
    {
        foreach (var job in jobs) yield return job;
        await Task.CompletedTask;
    }

    private static CollectedJob Job(string title, string url = "", string company = "Acme", string description = "We are looking for a developer with experience to work with the team on the product for you.", string filterId = "") =>
        new("Fake", Guid.NewGuid().ToString("N"), title, company, "Remote", description, url.Length > 0 ? url : $"https://jobs.test/{title.Replace(' ', '-')}", "2026-11-20T10:00:00+00:00", filterId);

    private static JobCollector Collector(TempDb t, JobSources.Provider provider, Func<int?, string>? evaluate = null, string name = "fake")
    {
        new SettingsStore(t.Db).SetMany(new Dictionary<string, string> { ["sources"] = name, ["keywords"] = "" });
        return new JobCollector(t.Db, new Dictionary<string, JobSources.Provider> { [name] = provider }, evaluateNew: evaluate);
    }

    private static async Task RunAsync(JobCollector collector, int? maxApply = null)
    {
        Assert.True(collector.Start(maxApply));
        await collector.WhenIdle().WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Saves_new_jobs_once_by_fingerprint_and_records_the_run()
    {
        using var t = new TempDb();
        var collector = Collector(t, _ => Jobs(Job("Dev A"), Job("Dev B"), Job("Dev A")));
        await RunAsync(collector);
        Assert.Equal(2, t.Scalar<int>("SELECT COUNT(*) FROM jobs"));
        Assert.Equal("en", t.Scalar<string>("SELECT language FROM jobs WHERE title='Dev A'"));
        Assert.Equal("new", t.Scalar<string>("SELECT status FROM jobs WHERE title='Dev A'"));
        Assert.Equal("completed", t.Scalar<string>("SELECT state FROM runs"));
        Assert.Equal(2, t.Scalar<int>("SELECT found_count FROM runs"));
        var snapshot = collector.Snapshot();
        Assert.Equal(("stopped", 2), (snapshot.State, snapshot.LastFound));
        Assert.Equal("Busca concluída: 2 vaga(s) nova(s). Clique em Iniciar para executar outra vez.", snapshot.Message);
        Assert.Contains(t.Changes, c => c is JobsChanged { Ids.Count: 1 });
        Assert.Contains(t.Changes, c => c is CollectorChanged);

        await RunAsync(collector); // nova rodada: tudo já existe
        Assert.Equal(2, t.Scalar<int>("SELECT COUNT(*) FROM jobs"));
        Assert.Equal(0, collector.Snapshot().LastFound);
    }

    [Fact]
    public async Task Keyword_terms_match_whole_words_and_linkedin_filters_bypass_them()
    {
        using var t = new TempDb();
        var collector = Collector(t, _ => Jobs(Job("QA Analyst"), Job("Aqua Operator"), Job("Javascript Dev"), Job("Java Dev"), Job("Cargo Qualquer", filterId: "main-abc")));
        new SettingsStore(t.Db).Set("keywords", "qa, java");
        await RunAsync(collector);
        var titles = t.Db.Open().Query<string>("SELECT title FROM jobs ORDER BY id").ToList();
        Assert.Equal(["QA Analyst", "Java Dev", "Cargo Qualquer"], titles);
        Assert.Equal("main-abc", t.Scalar<string>("SELECT search_filter_id FROM jobs WHERE title='Cargo Qualquer'"));
    }

    [Fact]
    public async Task Limit_closes_the_source_so_no_further_batches_start()
    {
        using var t = new TempDb();
        var produced = 0;
        var closed = false;
        async IAsyncEnumerable<CollectedJob> Source()
        {
            try
            {
                for (var i = 0; i < 10; i++) { produced++; yield return Job($"Dev {i}"); }
            }
            finally { closed = true; }
            await Task.CompletedTask;
        }
        var collector = Collector(t, _ => Source());
        await RunAsync(collector, maxApply: 2);
        Assert.Equal(2, t.Scalar<int>("SELECT COUNT(*) FROM jobs"));
        Assert.True(closed);
        Assert.Equal(2, produced);
        Assert.Equal("Busca concluída: 2 vaga(s) nova(s). Limite de 2 atingido; coleta encerrada. Clique em Iniciar para executar outra vez.", collector.Snapshot().Message);
    }

    [Fact]
    public async Task A_failing_or_unknown_source_is_reported_without_stopping_the_others()
    {
        using var t = new TempDb();
        async IAsyncEnumerable<CollectedJob> Boom()
        {
            await Task.CompletedTask;
            throw new ActionException("sem token");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        new SettingsStore(t.Db).Set("sources", "fake, missing ,boom");
        var collector = new JobCollector(t.Db, new Dictionary<string, JobSources.Provider>
        {
            ["fake"] = _ => Jobs(Job("Dev A")), ["boom"] = _ => Boom(),
        });
        await RunAsync(collector);
        Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM jobs"));
        Assert.Contains("Avisos: Fonte desconhecida: missing; boom: ValueError", collector.Snapshot().Message);
        Assert.Equal("warning", t.Scalar<string>("SELECT level FROM event_logs WHERE message LIKE 'Busca concluída%'"));
        Assert.Equal("Falha ao consultar boom: sem token", t.Scalar<string>("SELECT message FROM event_logs WHERE level='error'"));
    }

    [Fact]
    public async Task Stop_ends_the_cycle_early_and_marks_the_run_as_stopped()
    {
        using var t = new TempDb();
        var waiting = new TaskCompletionSource();
        async IAsyncEnumerable<CollectedJob> Slow([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken stop)
        {
            yield return Job("Primeira");
            waiting.SetResult();
            try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
            yield return Job("Nunca salva");
        }
        var collector = Collector(t, Slow);
        Assert.True(collector.Start());
        Assert.False(collector.Start()); // já rodando
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(20));
        collector.Stop();
        Assert.Equal("stopping", collector.Snapshot().State);
        await collector.WhenIdle().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(["Primeira"], t.Db.Open().Query<string>("SELECT title FROM jobs").ToList());
        Assert.Equal("stopped", t.Scalar<string>("SELECT state FROM runs"));
        Assert.Equal("Coleta interrompida. Clique em Iniciar para executar outra vez.", collector.Snapshot().Message);
    }

    [Fact]
    public async Task Auto_apply_sends_the_new_jobs_to_the_triage_queue_and_respects_the_limit()
    {
        using var t = new TempDb();
        var collector = Collector(t, _ => Jobs(Job("Dev A"), Job("Dev B")));
        new SettingsStore(t.Db).Set("auto_apply", "1");
        await RunAsync(collector);
        Assert.Equal(2, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs WHERE kind='job_apply'"));
        Assert.Equal(2, t.Scalar<int>("SELECT COUNT(*) FROM jobs WHERE status='review'"));
        Assert.Contains("Auto-apply: 2 vaga(s) enfileirada(s) para triagem", collector.Snapshot().Message);

        int? received = -1;
        var again = Collector(t, _ => Jobs(Job("Dev C")), evaluate: limit => { received = limit; return "nota"; });
        new SettingsStore(t.Db).Set("auto_apply", "1");
        await RunAsync(again, maxApply: 5);
        Assert.Equal(5, received);
        Assert.EndsWith("Auto-apply: nota Clique em Iniciar para executar outra vez.", again.Snapshot().Message);

        new SettingsStore(t.Db).Set("auto_apply", "0");
        received = -1;
        await RunAsync(again);
        Assert.Equal(-1, received);
        Assert.Contains(t.Db.Open().Query<string>("SELECT message FROM event_logs").ToList(), m => m == "auto_apply desligado; triagem automática ignorada neste ciclo.");
    }

    [Fact]
    public async Task The_portfolio_sync_runs_first_and_never_breaks_the_cycle()
    {
        using var t = new TempDb();
        new SettingsStore(t.Db).SetMany(new Dictionary<string, string> { ["sources"] = "fake", ["keywords"] = "" });
        var synced = false;
        var collector = new JobCollector(t.Db, new Dictionary<string, JobSources.Provider> { ["fake"] = _ => Jobs(Job("Dev A")) },
            beforeCycle: _ => { synced = true; throw new InvalidOperationException("portfólio fora do ar"); });
        await RunAsync(collector);
        Assert.True(synced);
        Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM jobs"));
    }
}

public class JobSourcesTests
{
    private const string UsageOk = """{"data":{"totalUsageCreditsUsdAfterVolumeDiscount":1.5,"usageCycle":{"startAt":"2026-11-01T00:00:00.000Z","endAt":"2026-11-30T23:59:59.999Z"}}}""";

    private static (TempDb T, RouteHandler Http, JobSources Sources) Setup(Action<RouteHandler>? routes = null, Dictionary<string, string>? settings = null, string token = "TOKEN-1")
    {
        var t = new TempDb();
        var http = new RouteHandler();
        routes?.Invoke(http);
        new SettingsStore(t.Db).SetMany(settings ?? []);
        var sources = new JobSources(t.Db, new ApifyClient(new HttpClient(http)), name => name == "apify_token" ? token : name.StartsWith("adzuna") ? "adz-" + name : "",
            apifyOptions: new ApifyClient.Options(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(30)));
        return (t, http, sources);
    }

    private static async Task<List<CollectedJob>> Collect(JobSources.Provider provider) => await provider(CancellationToken.None).ToListAsync();

    private static string MainFilter(params string[] keywords) =>
        LinkedInFilters.ValidateForm(new JsonObject { ["name"] = "Software Engineer", ["keywords"] = new JsonArray(keywords.Select(k => (JsonNode?)k).ToArray()), ["experience_levels"] = new JsonArray(3, 4) }.ToJsonString(), "main");

    [Fact]
    public async Task Remotive_and_remoteok_feeds_become_common_jobs()
    {
        var (t, http, sources) = Setup(h => h
            .On("GET", "remotive.com", """{"jobs":[{"id":7,"title":"Dev","company_name":"Co","candidate_required_location":"Worldwide","description":"<p>x</p>","url":"https://r/7","publication_date":"2026-11-01T00:00:00"}]}""")
            .On("GET", "remoteok.com", """[{"legal":"aviso"},{"id":9,"position":"Eng","company":"Ok","location":"","description":"d","url":"https://ok/9","date":"2026-11-02T00:00:00+00:00"}]"""));
        using var _ = t;
        var remotive = Assert.Single(await Collect(sources.Remotive));
        Assert.Equal(new CollectedJob("Remotive", "7", "Dev", "Co", "Worldwide", "<p>x</p>", "https://r/7", "2026-11-01T00:00:00"), remotive);
        var ok = Assert.Single(await Collect(sources.RemoteOk));
        Assert.Equal(new CollectedJob("Remote OK", "9", "Eng", "Ok", "", "d", "https://ok/9", "2026-11-02T00:00:00+00:00"), ok);
        Assert.All(http.Requests, r => Assert.Equal(ApifyClient.UserAgent, r.UserAgent));
    }

    [Fact]
    public async Task Adzuna_asks_each_country_and_keyword_and_requires_credentials()
    {
        var (t, http, sources) = Setup(h => h.On("GET", "api.adzuna.com", """{"results":[{"id":"a1","title":"T","company":{"display_name":"C"},"location":{"display_name":"L"},"description":"d","redirect_url":"https://adz/1","created":"2026-11-03"}]}"""),
            new() { ["keywords"] = "python, c#, go", ["adzuna_countries"] = "br, US" });
        using var _ = t;
        var jobs = await Collect(sources.Adzuna);
        Assert.Equal(4, jobs.Count); // 2 países × 2 primeiras palavras-chave
        Assert.Equal(new CollectedJob("Adzuna", "a1", "T", "C", "L", "d", "https://adz/1", "2026-11-03"), jobs[0]);
        Assert.Contains("/jobs/br/search/1?app_id=adz-adzuna_app_id&app_key=adz-adzuna_app_key&results_per_page=50&what=python", http.Requests[0].Url);
        Assert.Contains("/jobs/us/search/1", http.Requests[2].Url);
        Assert.Contains("what=c%23", http.Requests[1].Url);

        var bare = new JobSources(t.Db, new ApifyClient(new HttpClient(http)), _ => "");
        var error = await Assert.ThrowsAsync<CollectorException>(() => Collect(bare.Adzuna));
        Assert.Equal(("ValueError", "Configure Adzuna app_id e app_key no painel."), (error.PythonName, error.Message));
    }

    [Fact]
    public async Task Http_errors_use_the_python_messages()
    {
        var (t, _, sources) = Setup(h => h.OnStatus("GET", "remotive.com", (503, "indisponível")).OnStatus("GET", "remoteok.com", (500, "")));
        using var _ = t;
        var first = await Assert.ThrowsAsync<CollectorException>(() => Collect(sources.Remotive));
        Assert.Equal(("RuntimeError", "HTTP 503: indisponível"), (first.PythonName, first.Message));
        var second = await Assert.ThrowsAsync<CollectorException>(() => Collect(sources.RemoteOk));
        Assert.Equal("HTTP 500: Internal Server Error", second.Message);
    }

    [Fact]
    public async Task Apify_runs_the_planned_actor_tags_the_jobs_and_drops_closed_and_indian_ones()
    {
        var items = """
            [{"id":"1","title":"Backend Engineer","companyName":"Acme","location":"Brazil","descriptionText":"desc","link":"https://li/1","postedAt":"3 hours ago"},
             {"id":"2","title":"Dev Mumbai","companyName":"Ind","location":"Mumbai, India","link":"https://li/2","postedAt":"3 hours ago"},
             {"id":"3","title":"Fechada","companyName":"Z","location":"Brazil","link":"https://li/3","closed":true},
             {"id":"4","title":"Muito velha","companyName":"Z","location":"Brazil","link":"https://li/4","postedAt":"3 weeks ago"}]
            """;
        var (t, http, sources) = Setup(h => h
            .On("GET", "users/me/usage/monthly", UsageOk)
            .On("POST", "actors/curious_coder~linkedin-jobs-scraper/runs", """{"data":{"id":"run1","status":"READY","defaultDatasetId":"ds1"}}""")
            .On("GET", "actor-runs/run1", """{"data":{"status":"RUNNING"}}""", """{"data":{"status":"SUCCEEDED","defaultDatasetId":"ds1"}}""")
            .On("GET", "datasets/ds1/items?clean=1", items),
            new() { ["linkedin_main_filter_json"] = MainFilter("Backend Engineer") });
        using var _ = t;
        var jobs = await Collect(sources.Apify);
        Assert.Equal(2, jobs.Count); // uma execução por janela de publicação: 24h e 7 dias
        Assert.Equal(["Apify:LinkedIn Jobs: Software Engineer (24h, lote 1)", "Apify:LinkedIn Jobs: Software Engineer (7d, lote 1)"], jobs.Select(j => j.Source));
        Assert.All(jobs, job => Assert.Equal(("Backend Engineer", "main-", "1"), (job.Title, job.FilterId[..5], job.SearchRemote)));
        var posts = http.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(2, posts.Count);
        var start = posts[0];
        var input = JsonNode.Parse(start.Body!)!.AsObject();
        Assert.Equal("TOKEN-1", start.Bearer);
        Assert.Equal(25, input["count"]!.GetValue<int>());
        Assert.False(input["scrapeCompany"]!.GetValue<bool>());
        Assert.All(input["urls"]!.AsArray(), u => Assert.StartsWith("https://www.linkedin.com/jobs/search/?keywords=Backend+Engineer&location=", u!.GetValue<string>()));
        var settings = new SettingsStore(t.Db);
        Assert.Equal("1.5000", settings.Get("apify_last_usage_usd"));
        Assert.Equal("01/11/2026 a 30/11/2026", settings.Get("apify_last_usage_cycle"));
        var logs = t.Db.Open().Query<string>("SELECT message FROM event_logs ORDER BY id").ToList();
        Assert.Contains("Coleta planejada: Principal (1 filtro(s)), 2 execução(ões) Apify.", logs);
        Assert.Contains("LinkedIn Jobs: Software Engineer (24h, lote 1): 1 vaga(s) descartada(s) no filtro (fechadas / sem candidaturas).", logs);
        Assert.Contains("LinkedIn Jobs: Software Engineer (24h, lote 1): 1 vaga(s) da Índia descartada(s) antes de entrar na coleta.", logs);
        Assert.Contains(logs, m => m.StartsWith("Actor LinkedIn Jobs: ") && m.EndsWith("vaga(s) úteis."));
    }

    [Fact]
    public async Task Apify_is_blocked_by_missing_token_zero_limit_or_reached_limit()
    {
        var (t, _, none) = Setup(token: "");
        using (t) Assert.Equal("Configure o token da Apify no painel.", (await Assert.ThrowsAsync<CollectorException>(() => Collect(none.Apify))).Message);

        var (t2, _, zero) = Setup(settings: new() { ["apify_monthly_credit_limit_usd"] = "0" });
        using (t2) Assert.Equal("Limite mensal de créditos Apify está em zero; a fonte não será consultada.", (await Assert.ThrowsAsync<CollectorException>(() => Collect(zero.Apify))).Message);

        var (t3, http, reached) = Setup(h => h.On("GET", "users/me/usage/monthly", UsageOk), new() { ["apify_monthly_credit_limit_usd"] = "1,5" });
        using (t3)
        {
            var error = await Assert.ThrowsAsync<CollectorException>(() => Collect(reached.Apify));
            Assert.Equal("Limite mensal de créditos Apify atingido (1.50 USD de 1.50 USD no ciclo 01/11/2026 a 30/11/2026).", error.Message);
            Assert.DoesNotContain(http.Requests, r => r.Method == "POST"); // nenhum run pago começou
        }
    }

    [Fact]
    public async Task Apify_without_a_main_filter_starts_no_linkedin_run()
    {
        var (t, http, sources) = Setup(h => h.On("GET", "users/me/usage/monthly", UsageOk));
        using var _ = t;
        Assert.Empty(await Collect(sources.Apify));
        Assert.DoesNotContain(http.Requests, r => r.Method == "POST");
        Assert.Contains("Sem filtro Principal: nenhum run LinkedIn será iniciado. Gere o filtro Principal no painel.", t.Db.Open().Query<string>("SELECT message FROM event_logs"));
    }

    [Fact]
    public async Task Apify_failures_are_collected_and_raised_only_when_nothing_worked()
    {
        var (t, _, sources) = Setup(h => h
            .On("GET", "users/me/usage/monthly", UsageOk)
            .On("POST", "/runs", """{"data":{"id":"run1","status":"READY","defaultDatasetId":"ds1"}}""")
            .On("GET", "actor-runs/run1", """{"data":{"status":"FAILED"}}"""),
            new() { ["linkedin_main_filter_json"] = MainFilter("Backend Engineer") });
        using var _ = t;
        var error = await Assert.ThrowsAsync<CollectorException>(() => Collect(sources.Apify));
        Assert.Equal("RuntimeError", error.PythonName);
        Assert.StartsWith("Todos os actors Apify falharam: LinkedIn Jobs: Software Engineer (", error.Message);
        Assert.EndsWith("encerrada com status FAILED.", error.Message);
        Assert.Contains(t.Db.Open().Query<string>("SELECT message FROM event_logs WHERE level='error'"), m => m.StartsWith("Falha no actor LinkedIn Jobs:"));
    }

    [Fact]
    public async Task Apify_stops_before_a_new_paid_batch_when_the_credit_check_says_the_limit_was_reached()
    {
        var (t, http, sources) = Setup(h => h
            .On("GET", "users/me/usage/monthly",
                UsageOk, // início: abaixo do limite
                UsageOk.Replace("1.5", "9.5")) // antes do 1º lote: limite estourado
            .On("POST", "/runs", """{"data":{"id":"r"}}"""),
            new() { ["linkedin_main_filter_json"] = MainFilter("Backend Engineer") });
        using var _ = t;
        var error = await Assert.ThrowsAsync<CollectorException>(() => Collect(sources.Apify));
        Assert.Contains("limite de créditos atingido antes da execução", error.Message);
        Assert.DoesNotContain(http.Requests, r => r.Method == "POST");
    }
}
