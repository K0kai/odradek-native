using System.Net;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

public class IndeedImportTests
{
    private static IndeedCard Card(string id, string title = "Dev Pleno", string company = "Acme", string location = "Recife, PE",
        string description = "<p>Requisitos: C# e SQL.</p>", string posted = "", string host = "br.indeed.com") =>
        new(id, title, company, location, description, posted, host);

    [Fact]
    public void Job_keys_are_validated_and_read_from_indeed_urls()
    {
        Assert.True(IndeedImport.IsValidId("0123456789abcdef"));
        foreach (var bad in new[] { "", "abc", "0123456789ABCDEF", "0123456789abcdeg", "0123456789abcdef0", "0123456789abcdef\n", "../../0123456789ab" })
            Assert.False(IndeedImport.IsValidId(bad), bad);
        foreach (var url in new[]
                 {
                     "https://br.indeed.com/viewjob?jk=0123456789abcdef", "https://www.indeed.com/rc/clk?jk=0123456789abcdef&from=serp",
                     "https://br.indeed.com/pagead/clk?mo=r&jk=0123456789ABCDEF", "https://uk.indeed.co.uk/viewjob?from=x&jk=0123456789abcdef",
                 })
            Assert.Equal("0123456789abcdef", IndeedImport.ExtractJobKey(url));
        foreach (var url in new[] { "", "0123456789abcdef", "https://br.indeed.com/jobs?q=dev", "https://example.com/viewjob?jk=0123456789abcdef", "https://br.indeed.com/viewjob?jk=0123456789abcdef00" })
            Assert.Null(IndeedImport.ExtractJobKey(url));
    }

    [Fact]
    public void New_jobs_are_saved_with_a_canonical_url_and_repeats_are_duplicates()
    {
        using var t = new TempDb();
        var summary = IndeedImport.Run(t.Db, [Card("aaaaaaaaaaaaaaa1", posted: "há 3 dias"), Card("aaaaaaaaaaaaaaa1")]);

        Assert.Equal(new ImportSummary(2, 1, 1, 0, 0, 0, ""), summary);
        using var c = t.Db.Open();
        var job = c.QuerySingle<(string Source, string SourceId, string Url, string Filter, string Description, string? Posted)>(
            "SELECT source, source_id, url, search_filter_id, description, posted_at FROM jobs");
        Assert.Equal(("Indeed:Recomendadas", "aaaaaaaaaaaaaaa1", "https://br.indeed.com/viewjob?jk=aaaaaaaaaaaaaaa1", "rec-indeed"), (job.Source, job.SourceId, job.Url, job.Filter));
        Assert.Contains("Requisitos: C# e SQL.", job.Description);
        Assert.StartsWith(DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd"), job.Posted);
        Assert.Contains(t.Changes, change => change is JobsChanged);

        Assert.Equal(new ImportSummary(1, 1, 0, 0, 0, 0, ""), IndeedImport.Run(t.Db, [Card("aaaaaaaaaaaaaaa1")]));
    }

    [Theory]
    [InlineData("2026-11-20", "2026-11-20")]
    [InlineData("2026-11-20T10:00:00Z", "2026-11-20")]
    [InlineData("", null)]
    [InlineData("Postagem recente", null)]
    public void The_posted_date_accepts_iso_and_ignores_unknown_texts(string posted, string? day)
    {
        using var t = new TempDb();
        IndeedImport.Run(t.Db, [Card("aaaaaaaaaaaaaaa2", posted: posted)]);
        Assert.Equal(day, t.Scalar<string?>("SELECT substr(posted_at,1,10) FROM jobs"));
    }

    [Fact]
    public void Jobs_that_came_from_apify_are_already_known()
    {
        using var t = new TempDb();
        using (var c = t.Db.Open())
        {
            c.Execute("INSERT INTO jobs(source,source_id,title,company,location,description,url,first_seen_at,fingerprint) VALUES('Apify:Indeed','x','Dev','Acme','BR','d','https://www.indeed.com/viewjob?jk=bbbbbbbbbbbbbbb1','2026-11-01T00:00:00+00:00','fp1')");
            // a URL é a do site da empresa: o jobkey só está no source_id
            c.Execute("INSERT INTO jobs(source,source_id,title,company,location,description,url,first_seen_at,fingerprint) VALUES('Apify:Indeed','bbbbbbbbbbbbbbb2','Dev','Foo','BR','d','https://foo.example/careers/1','2026-11-01T00:00:00+00:00','fp2')");
        }
        var summary = IndeedImport.Run(t.Db, [Card("bbbbbbbbbbbbbbb1"), Card("bbbbbbbbbbbbbbb2")]);
        Assert.Equal(new ImportSummary(2, 2, 0, 0, 0, 0, ""), summary);
        Assert.Equal(2, t.Scalar<long>("SELECT COUNT(*) FROM jobs"));
    }

    [Fact]
    public void Excluded_region_closed_and_untitled_jobs_are_not_saved()
    {
        using var t = new TempDb();
        var summary = IndeedImport.Run(t.Db,
        [
            Card("ccccccccccccccc1", location: "Bangalore, India", description: "Join our team in Bangalore."),
            Card("ccccccccccccccc2", description: "No longer accepting applications"),
            Card("ccccccccccccccc3", title: ""),
        ]);
        Assert.Equal(new ImportSummary(3, 0, 0, 2, 1, 0, ""), summary);
        Assert.Equal(0, t.Scalar<long>("SELECT COUNT(*) FROM jobs"));
    }

    [Fact]
    public void Triage_runs_only_with_auto_apply_on()
    {
        using var t = new TempDb();
        var calls = 0;
        string Evaluate() { calls++; return "1 vaga(s) enfileirada(s)"; }

        new SettingsStore(t.Db).Set("auto_apply", "0");
        Assert.Equal("", IndeedImport.Run(t.Db, [Card("ddddddddddddddd1")], Evaluate).Triage);
        new SettingsStore(t.Db).Set("auto_apply", "1");
        Assert.Equal("1 vaga(s) enfileirada(s)", IndeedImport.Run(t.Db, [Card("ddddddddddddddd2", title: "Dev Sênior")], Evaluate).Triage);
        Assert.Equal(1, calls);
    }
}

public class IndeedQueueTests
{
    private static IndeedPending Pending(int n) => new($"{n:x16}", $"Dev {n}", "Acme", "Recife", "br.indeed.com");

    [Fact]
    public void Announced_jobs_leave_the_queue_as_they_get_an_outcome()
    {
        using var t = new TempDb();
        var queue = new IndeedQueue(t.Db);
        queue.Announce(Enumerable.Range(1, 7).Select(Pending).Append(Pending(1)));
        var start = queue.Snapshot();
        Assert.Equal((IndeedState.Receiving, 7, IndeedQueue.BatchSize), (start.State, start.Pending.Count, start.CurrentBatch.Count));
        Assert.Contains(t.Changes, change => change is IndeedQueueChanged);

        queue.Report(new ImportOutcome(Pending(1).Id, ImportResult.Inserted, "", 10, "Dev 1"), "br.indeed.com");
        queue.Skip(Pending(2).Id, ImportResult.Dropped, "a vaga não existe mais");
        queue.Skip("ffffffffffffffff", ImportResult.Failed, "id que não está na fila");
        queue.Finish(IndeedState.Blocked);

        var end = queue.Snapshot();
        Assert.Equal((IndeedState.Blocked, 5, 1, 1, 0), (end.State, end.Pending.Count, end.Inserted, end.Dropped, end.Failed));
        Assert.Empty(end.CurrentBatch);
        Assert.Equal(["Dev 2", "Dev 1"], end.History.Select(item => item.Outcome.Title));
    }

    [Fact]
    public void Silence_while_receiving_shows_as_stalled()
    {
        using var t = new TempDb();
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        var queue = new IndeedQueue(t.Db, () => now);
        queue.Announce([Pending(1)]);
        now += IndeedQueue.StallAfter + TimeSpan.FromSeconds(1);
        Assert.Equal(IndeedState.Stalled, queue.Snapshot().State);
        Assert.Empty(queue.Snapshot().CurrentBatch);
    }

    [Fact]
    public void Clearing_asks_the_extension_to_stop_until_the_next_list()
    {
        using var t = new TempDb();
        var queue = new IndeedQueue(t.Db);
        Assert.Equal(0, queue.ClearPending());
        Assert.False(queue.Cancelled);

        queue.Announce([Pending(1), Pending(2)]);
        Assert.Equal(2, queue.ClearPending());
        queue.Finish(IndeedState.Done); // o aviso do envio cancelado não muda o estado
        Assert.Equal((true, IndeedState.Idle), (queue.Cancelled, queue.Snapshot().State));

        queue.Announce([Pending(3)]);
        Assert.False(queue.Cancelled);
    }
}

public class IndeedImportRouteTests
{
    [Fact]
    public async Task The_extension_progress_reaches_the_app_queue()
    {
        using var api = ApiHarness.Start();
        var token = new ExtensionTokenActions(api.Temp.Db).Issue();
        async Task<System.Text.Json.Nodes.JsonNode> Post(string body)
        {
            var (status, json) = await api.Json("POST", "/api/indeed-import", token, body);
            Assert.Equal(HttpStatusCode.OK, status);
            return json!;
        }

        var announced = await Post("""{"queue":[{"id":"abababababababa1","title":"Um","host":"br.indeed.com"},{"id":"abababababababa2","title":"Dois"},{"id":"abababababababa3","title":"Três"}]}""");
        Assert.Equal(3, announced["pending"]!.GetValue<int>());
        var saved = await Post($$"""{"jobs":[{{Job("abababababababa1")}}]}""");
        Assert.False(saved["cancelled"]!.GetValue<bool>());
        Assert.Equal(1, (await Post("""{"skipped":[{"id":"abababababababa2","reason":"unreadable"}]}"""))["pending"]!.GetValue<int>());
        Assert.Equal(1, (await Post("""{"finished":"blocked"}"""))["pending"]!.GetValue<int>());

        foreach (var bad in new[]
                 {
                     """{"queue":[{"id":"x"}]}""", """{"queue":"x"}""", """{"skipped":[{"id":"abababababababa3","reason":"outro"}]}""",
                     """{"skipped":["x"]}""", """{"finished":"outro"}""",
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Json("POST", "/api/indeed-import", token, bad)).Status);
        Assert.Equal(1, api.Temp.Scalar<long>("SELECT COUNT(*) FROM jobs")); // os avisos não gravam vagas
    }

    private static string Job(string id, string extra = "") => $$"""{"id":"{{id}}","title":"Dev","company":"Acme","location":"Recife","description":"Descrição completa."{{extra}}}""";

    [Fact]
    public async Task The_route_needs_the_pairing_token_and_validates_the_body()
    {
        using var api = ApiHarness.Start();
        var body = $$"""{"jobs":[{{Job("eeeeeeeeeeeeeee1")}}]}""";
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/indeed-import", token: null, body: body)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Json("POST", "/api/indeed-import", token: ApiHarness.QpidToken, body: body)).Status);

        var token = new ExtensionTokenActions(api.Temp.Db).Issue();
        foreach (var bad in new[]
                 {
                     "{}", """{"jobs":[]}""", """{"jobs":"x"}""", """{"ids":[]}""", """{"ids":["../x"]}""",
                     $$"""{"jobs":[{{Job("../x")}}]}""",
                     """{"jobs":[{"id":"eeeeeeeeeeeeeee1","title":"Dev"}]}""", // sem descrição
                     $$"""{"jobs":[{{Job("eeeeeeeeeeeeeee1", ",\"host\":\"evil.example\"")}}]}""",
                     $$"""{"jobs":[{{Job("eeeeeeeeeeeeeee1", ",\"host\":\"br.indeed.com.evil.example\"")}}]}""",
                     "{\"jobs\":[" + string.Join(',', Enumerable.Range(0, IndeedImport.MaxPerCall + 1).Select(i => Job($"{i:x16}"))) + "]}",
                     "{\"ids\":[" + string.Join(',', Enumerable.Range(0, IndeedImport.MaxIdsPerQuery + 1).Select(i => $"\"{i:x16}\"")) + "]}",
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Json("POST", "/api/indeed-import", token, bad)).Status);
        Assert.Equal(0, api.Temp.Scalar<long>("SELECT COUNT(*) FROM jobs"));
    }

    [Fact]
    public async Task The_extension_asks_for_new_ids_and_then_sends_the_complete_jobs()
    {
        using var api = ApiHarness.Start();
        var token = new ExtensionTokenActions(api.Temp.Db).Issue();
        var (status, json) = await api.Json("POST", "/api/indeed-import", token, $$"""{"jobs":[{{Job("fffffffffffffff1")}}]}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!["inserted"]!.GetValue<int>());
        Assert.Equal("https://www.indeed.com/viewjob?jk=fffffffffffffff1", api.Temp.Scalar<string>("SELECT url FROM jobs")); // sem host: o padrão

        var (_, asked) = await api.Json("POST", "/api/indeed-import", token, """{"ids":["fffffffffffffff1","fffffffffffffff2","fffffffffffffff2"]}""");
        Assert.Equal(new[] { "fffffffffffffff2" }, asked!["unknown"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(1, asked["known"]!.GetValue<int>());
        Assert.Empty(api.LinkedIn.Requests); // o app não busca nada fora
    }
}
