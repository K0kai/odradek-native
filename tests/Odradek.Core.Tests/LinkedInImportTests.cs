using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.Core.Tests;

public class LinkedInImportTests
{
    private const string Guest = """
        <section class="top-card-layout">
          <h2 class="top-card-layout__title font-sans text-lg">Engenheiro de Software &amp; Dados</h2>
          <a class="topcard__org-name-link topcard__flavor--black-link" href="https://www.linkedin.com/company/acme">
            Acme Ltda
          </a>
          <span class="topcard__flavor topcard__flavor--bullet">São Paulo, Brasil</span>
          <span class="posted-time-ago__text posted-time-ago__text--new">2 days ago</span>
        </section>
        <div class="show-more-less-html__markup show-more-less-html__markup--clamp-after-5">
          <strong>Requisitos</strong><br>Experiência com C# e SQL.
        </div>
        """;

    private static Task NoPause(CancellationToken _) => Task.CompletedTask;

    private static Func<string, CancellationToken, Task<(int Status, string Body)>> Fetch(RouteHandler handler)
    {
        var reverify = new WorthReverify(null!, new HttpClient(handler));
        return reverify.FetchUrlTextAsync;
    }

    private static ImportCard Card(string id, string title = "Dev", string company = "Acme", string location = "Brasil") => new(id, title, company, location);

    [Fact]
    public void Guest_html_gives_title_company_location_posted_and_description()
    {
        var parsed = LinkedInImport.ParseGuest(Guest);
        Assert.Equal("Engenheiro de Software & Dados", parsed.Title);
        Assert.Equal("Acme Ltda", parsed.Company);
        Assert.Equal("São Paulo, Brasil", parsed.Location);
        Assert.Equal("2 days ago", parsed.Posted);
        Assert.Contains("Experiência com C# e SQL.", parsed.Description);
        Assert.NotNull(LinkedInImport.PostedIso(parsed.Posted));
        Assert.Equal(("", "", "", "", ""), LinkedInImport.ParseGuest("<html>nada</html>"));
    }

    [Fact]
    public void Only_numeric_ids_become_cards()
    {
        Assert.True(LinkedInImport.IsValidId("4001234567"));
        foreach (var bad in new[] { "", "12", "40012a", "../../x", "4001 2345", "4001234567/../a" })
            Assert.False(LinkedInImport.IsValidId(bad), bad);
        Assert.Null(LinkedInImport.Card(JsonNode.Parse("""{"id":"abc","title":"x"}""")));
        Assert.Null(LinkedInImport.Card(JsonNode.Parse("""["4001234567"]""")));
        Assert.Equal(Card("4001234567", "Dev", "Acme", "Recife"), LinkedInImport.Card(JsonNode.Parse("""{"id":4001234567,"title":" Dev ","company":"Acme","location":"Recife"}""")));
    }

    [Fact]
    public async Task New_jobs_are_saved_with_the_public_page_data_and_known_ones_are_skipped()
    {
        using var t = new TempDb();
        using (var c = t.Db.Open()) // já veio do Apify, com outra forma de URL
            c.Execute("INSERT INTO jobs(source,source_id,title,company,location,description,url,first_seen_at,fingerprint) VALUES('Apify:x','x','Dev','Acme','BR','d','https://br.linkedin.com/jobs/view/senior-dev-at-acme-4000000002','2026-11-01T00:00:00+00:00','fp1')");
        var http = new RouteHandler().On("GET", "jobPosting/4000000001", Guest);
        var summary = await LinkedInImport.RunAsync(t.Db, [Card("4000000001"), Card("4000000002"), Card("4000000001")], Fetch(http), NoPause);

        Assert.Equal(new ImportSummary(3, 2, 1, 0, 0, 0, ""), summary);
        Assert.Single(http.Requests); // os conhecidos e os repetidos nem chegam ao LinkedIn
        Assert.Equal("https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/4000000001", http.Requests[0].Url);
        using var c2 = t.Db.Open();
        var job = c2.QuerySingle<(string Source, string Title, string Company, string Location, string Url, string Filter, string Description, string? Posted)>(
            "SELECT source, title, company, location, url, search_filter_id, description, posted_at FROM jobs WHERE source_id='4000000001'");
        Assert.Equal(("LinkedIn:Recomendadas", "Engenheiro de Software & Dados", "Acme Ltda", "São Paulo, Brasil", "https://www.linkedin.com/jobs/view/4000000001/", "rec-linkedin"), (job.Source, job.Title, job.Company, job.Location, job.Url, job.Filter));
        Assert.Contains("Experiência com C# e SQL.", job.Description);
        Assert.NotNull(job.Posted);
        Assert.Contains(t.Changes, c => c is JobsChanged);
        Assert.Equal("new", t.Scalar<string>("SELECT status FROM jobs WHERE source_id='4000000001'"));

        var again = await LinkedInImport.RunAsync(t.Db, [Card("4000000001")], Fetch(new RouteHandler()), NoPause); // segunda vez: tudo conhecido
        Assert.Equal(new ImportSummary(1, 1, 0, 0, 0, 0, ""), again);
    }

    [Fact]
    public async Task Cards_that_arrive_with_a_description_are_saved_without_touching_linkedin()
    {
        using var t = new TempDb();
        var http = new RouteHandler(); // qualquer pedido estoura: "rota não prevista"
        var posted = DateTime.UtcNow.AddDays(-3).ToString("o");
        var cards = new[]
        {
            Card("4000000060", "Dev Pleno", "Acme", "Belo Horizonte, MG") with { Description = "Requisitos: C# e SQL. Trabalho remoto.", Posted = posted },
            Card("4000000061", "Dev India", "Foo", "Bangalore, India") with { Description = "Join our team in Bangalore." },
            Card("4000000062", "", "Foo", "Brasil") with { Description = "Sem título." },
        };
        var summary = await LinkedInImport.RunAsync(t.Db, cards, Fetch(http), NoPause);
        Assert.Equal(new ImportSummary(3, 0, 1, 1, 1, 0, ""), summary);
        Assert.Empty(http.Requests);
        using var c = t.Db.Open();
        var job = c.QuerySingle<(string Title, string Description, string Url, string Filter, string? Posted)>("SELECT title, description, url, search_filter_id, posted_at FROM jobs WHERE source_id='4000000060'");
        Assert.Equal(("Dev Pleno", "Requisitos: C# e SQL. Trabalho remoto.", "https://www.linkedin.com/jobs/view/4000000060/", "rec-linkedin"), (job.Title, job.Description, job.Url, job.Filter));
        Assert.StartsWith(DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd"), job.Posted);
        // sem descrição e depois de um 429, as completas ainda entram
        var mixed = await LinkedInImport.RunAsync(t.Db, [Card("4000000063"), Card("4000000064") with { Description = "Outra vaga completa." }], Fetch(new RouteHandler().OnStatus("GET", "jobPosting/", (429, ""))), NoPause);
        Assert.Equal(new ImportSummary(2, 0, 1, 0, 0, 1, ""), mixed);
    }

    [Fact]
    public async Task Closed_missing_blocked_and_broken_pages_are_counted_not_saved()
    {
        using var t = new TempDb();
        var http = new RouteHandler()
            .OnStatus("GET", "jobPosting/4000000010", (404, ""))
            .On("GET", "jobPosting/4000000011", "<h2 class=\"top-card-layout__title\">Dev</h2><figure class=\"closed-job\">No longer accepting applications</figure>")
            .OnStatus("GET", "jobPosting/4000000012", (500, "erro"))
            .On("GET", "jobPosting/4000000013", "<h2 class=\"top-card-layout__title\">Dev</h2><span class=\"topcard__flavor--bullet\">Bangalore, India</span>");
        var summary = await LinkedInImport.RunAsync(t.Db, [Card("4000000010"), Card("4000000011"), Card("4000000012"), Card("4000000013")], Fetch(http), NoPause);
        Assert.Equal(new ImportSummary(4, 0, 0, 3, 1, 0, ""), summary);
        Assert.Equal(0, t.Scalar<long>("SELECT COUNT(*) FROM jobs"));
    }

    [Fact]
    public async Task A_429_stops_the_import_and_reports_the_rest_as_pending()
    {
        using var t = new TempDb();
        var http = new RouteHandler()
            .On("GET", "jobPosting/4000000020", Guest)
            .OnStatus("GET", "jobPosting/4000000021", (429, "slow down"))
            .On("GET", "jobPosting/4000000022", Guest);
        var summary = await LinkedInImport.RunAsync(t.Db, [Card("4000000020"), Card("4000000021"), Card("4000000022"), Card("4000000023")], Fetch(http), NoPause);
        Assert.Equal(new ImportSummary(4, 0, 1, 0, 0, 3, ""), summary);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Each_finished_card_is_reported_and_pending_ones_are_not()
    {
        using var t = new TempDb();
        var http = new RouteHandler()
            .On("GET", "jobPosting/4000000070", Guest)
            .On("GET", "jobPosting/4000000071", "<h2 class=\"top-card-layout__title\">Dev</h2><figure class=\"closed-job\">No longer accepting applications</figure>")
            .OnStatus("GET", "jobPosting/4000000072", (429, ""));
        List<ImportOutcome> reported = [];
        await LinkedInImport.RunAsync(t.Db, [Card("4000000070"), Card("4000000070"), Card("4000000071"), Card("4000000072"), Card("4000000073")],
            Fetch(http), NoPause, report: reported.Add);

        Assert.Equal([("4000000070", ImportResult.Inserted), ("4000000070", ImportResult.Duplicate), ("4000000071", ImportResult.Dropped)],
            reported.Select(o => (o.Id, o.Kind)));
        Assert.Equal(t.Scalar<long>("SELECT id FROM jobs WHERE source_id='4000000070'"), reported[0].JobId);
        Assert.Equal("Engenheiro de Software & Dados", reported[0].Title);
        Assert.Equal("a vaga foi encerrada", reported[2].Detail);
        Assert.All(reported.Skip(1), o => Assert.Null(o.JobId));
    }

    [Fact]
    public async Task Triage_runs_only_with_auto_apply_on()
    {
        using var t = new TempDb();
        var http = new RouteHandler().On("GET", "jobPosting/", Guest);
        var calls = 0;
        string Evaluate() { calls++; return "1 vaga(s) enfileirada(s)"; }

        new SettingsStore(t.Db).Set("auto_apply", "0");
        var off = await LinkedInImport.RunAsync(t.Db, [Card("4000000030")], Fetch(http), NoPause, Evaluate);
        Assert.Equal(("", 0), (off.Triage, calls));

        new SettingsStore(t.Db).Set("auto_apply", "1");
        var on = await LinkedInImport.RunAsync(t.Db, [Card("4000000031")], Fetch(http), NoPause, Evaluate);
        Assert.Equal(("1 vaga(s) enfileirada(s)", 1), (on.Triage, calls));
    }
}

public class LinkedInImportRouteTests
{
    private const string Page = "<h2 class=\"top-card-layout__title\">Dev Pleno</h2><span class=\"topcard__flavor--bullet\">Brasil</span><div class=\"show-more-less-html__markup\">Descrição</div>";

    [Fact]
    public async Task The_route_needs_the_pairing_token_and_validates_the_body()
    {
        using var api = ApiHarness.Start();
        var body = """{"jobs":[{"id":"4000000040","title":"Dev"}]}""";
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/linkedin-import", token: null, body: body)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/linkedin-import", token: ApiHarness.QpidToken, body: body)).Status);

        var token = new ExtensionTokenActions(api.Temp.Db).Issue();
        foreach (var bad in new[] { "{}", """{"jobs":[]}""", """{"jobs":[{"id":"../x"}]}""", """{"jobs":"x"}""",
                     "{\"jobs\":[" + string.Join(',', Enumerable.Range(0, LinkedInImport.MaxPerCall + 1).Select(i => $"{{\"id\":\"{4000100000 + i}\"}}")) + "]}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Json("POST", "/api/linkedin-import", token, bad)).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await api.Json("POST", "/api/linkedin-import", token, "[]")).Status);
        Assert.Empty(api.LinkedIn.Requests);
    }

    [Fact]
    public async Task The_extension_can_ask_which_ids_are_new_before_fetching_their_pages()
    {
        using var api = ApiHarness.Start();
        var token = new ExtensionTokenActions(api.Temp.Db).Issue();
        // uma vaga já conhecida, que entrou pelo Apify com outra forma de URL
        Assert.Equal(HttpStatusCode.OK, (await api.Json("POST", "/api/linkedin-import", token,
            """{"jobs":[{"id":"4000000080","title":"Dev","company":"Acme","location":"Recife","description":"Descrição completa."}]}""")).Status);

        var (status, json) = await api.Json("POST", "/api/linkedin-import", token, """{"ids":["4000000080","4000000081","4000000081","4000000082"]}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { "4000000081", "4000000082" }, json!["unknown"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(1, json["known"]!.GetValue<int>());
        Assert.Equal(0, api.Temp.Scalar<long>("SELECT COUNT(*) FROM jobs WHERE source_id IN ('4000000081','4000000082')")); // consultar não grava

        foreach (var bad in new[] { """{"ids":[]}""", """{"ids":["../x"]}""", "{\"ids\":[" + string.Join(',', Enumerable.Range(0, 501).Select(i => $"\"{4000200000 + i}\"")) + "]}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Json("POST", "/api/linkedin-import", token, bad)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/linkedin-import", token: null, body: """{"ids":["4000000081"]}""")).Status);
        Assert.Empty(api.LinkedIn.Requests);
    }

    [Fact]
    public async Task The_qpid_route_takes_the_qpid_token_and_imports_jobs_read_in_a_logged_in_session()
    {
        using var api = ApiHarness.Start();
        var body = """{"jobs":[{"id":"4000000070","title":"Dev","company":"Acme","location":"Recife","description":"Vaga completa lida na página.","posted":"2026-11-20T10:00:00+00:00"}]}""";
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/qpid/jobs/import-linkedin", token: null, body: body)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/qpid/jobs/import-linkedin", token: "outro", body: body)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Json("POST", "/api/qpid/jobs/import-linkedin", body: """{"jobs":[{"id":"../x"}]}""")).Status);
        var (status, json) = await api.Json("POST", "/api/qpid/jobs/import-linkedin", body: body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((1, 0), (json!["inserted"]!.GetValue<int>(), json["duplicates"]!.GetValue<int>()));
        Assert.Empty(api.LinkedIn.Requests);
        Assert.Equal("Vaga completa lida na página.", api.Temp.Scalar<string>("SELECT description FROM jobs WHERE source_id='4000000070'"));
        Assert.Equal("2026-11-20", api.Temp.Scalar<string>("SELECT substr(posted_at,1,10) FROM jobs WHERE source_id='4000000070'"));
        var (_, caps) = await api.Json("GET", "/api/qpid/capabilities");
        Assert.Contains("linkedin-import-v1", caps!["features"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(1, (await api.Json("POST", "/api/qpid/jobs/import-linkedin", body: body)).Json!["duplicates"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_valid_list_is_imported_and_summarised()
    {
        using var api = ApiHarness.Start();
        api.LinkedIn.On("GET", "jobPosting/4000000050", Page);
        var token = new ExtensionTokenActions(api.Temp.Db).Issue();
        var (status, json) = await api.Json("POST", "/api/linkedin-import", token, """{"page":"https://www.linkedin.com/jobs/collections/recommended/","jobs":[{"id":"4000000050","title":"Dev"}]}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!["inserted"]!.GetValue<int>());
        Assert.Equal(1, json["received"]!.GetValue<int>());
        Assert.Equal("Dev Pleno", api.Temp.Scalar<string>("SELECT title FROM jobs WHERE source_id='4000000050'"));
    }
}
