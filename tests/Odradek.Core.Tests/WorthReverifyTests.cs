using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Data;
using Odradek.Core.Engines;
using Odradek.Core.Queue;

namespace Odradek.Core.Tests;

public class WorthReverifyTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "liveness_cases.json")))!;

    [Fact]
    public void Http_classification_and_linkedin_ids_match_python()
    {
        foreach (var item in Cases["classify"]!.AsArray().OfType<JsonObject>())
            Assert.True(item["verdict"]!.GetValue<string>() == WorthReverify.ClassifyHttp(item["status"]!.GetValue<int>(), item["body"]!.GetValue<string>()), $"{item["status"]} / {item["body"]}");
        foreach (var item in Cases["ids"]!.AsArray().OfType<JsonObject>())
            Assert.Equal(item["id"]?.GetValue<string>(), WorthReverify.ExtractLinkedInJobId(item["url"]!.GetValue<string>()));
    }

    private static long AddWorth(TempDb t, string? url)
    {
        using var c = t.Db.Open();
        return c.ExecuteScalar<long>("INSERT INTO jobs(source,title,company,location,description,url,first_seen_at,status,fingerprint) VALUES('t','Dev','Acme','BR','d',@url,'2026-11-01T00:00:00+00:00','worth',hex(randomblob(8))) RETURNING id", new { url });
    }

    [Fact]
    public async Task Only_clear_evidence_of_a_closed_job_moves_it_to_ignored()
    {
        using var t = new TempDb();
        var closedLinkedIn = AddWorth(t, "https://www.linkedin.com/jobs/view/1001/");
        var openLinkedIn = AddWorth(t, "https://www.linkedin.com/jobs/view/1002/");
        var closedPage = AddWorth(t, "https://empresa.test/vaga/1");
        var openPage = AddWorth(t, "https://empresa.test/vaga/2");
        var blocked = AddWorth(t, "https://empresa.test/vaga/3");
        var gone = AddWorth(t, "https://empresa.test/vaga/4");
        var noUrl = AddWorth(t, "");
        var down = AddWorth(t, "https://empresa.test/vaga/5");
        var http = new RouteHandler()
            .OnStatus("GET", "jobPosting/1001", (404, "nao existe"))
            .On("GET", "jobPosting/1002", "<a class='topcard-apply'>x</a>")
            .On("GET", "empresa.test/vaga/1", "<p>This job is no longer available</p>")
            .On("GET", "empresa.test/vaga/2", "<button>Apply now</button>")
            .OnStatus("GET", "empresa.test/vaga/3", (403, "Cloudflare"))
            .OnStatus("GET", "empresa.test/vaga/4", (410, ""))
            .OnStatus("GET", "empresa.test/vaga/5", (200, "<html>sem sinal</html>"));
        var summary = await new WorthReverify(t.Db, new HttpClient(http)).ReverifyAsync();
        Assert.Equal(new ReverifySummary(8, 3, 2, 3, false), summary);
        string Status(long id) => t.Scalar<string>("SELECT status FROM jobs WHERE id=@id", new { id });
        Assert.Equal(["ignored", "worth", "ignored", "worth", "worth", "ignored", "worth", "worth"],
            new[] { closedLinkedIn, openLinkedIn, closedPage, openPage, blocked, gone, noUrl, down }.Select(Status));
        Assert.Equal("Ignorada na verificação diária (inativa): endpoint público HTTP 404 indica vaga encerrada", t.Scalar<string>("SELECT notes FROM jobs WHERE id=@id", new { id = closedLinkedIn }));
        Assert.Equal("Ignorada na verificação diária (inativa): HTTP 200", t.Scalar<string>("SELECT notes FROM jobs WHERE id=@id", new { id = closedPage }));
        Assert.Contains(t.Changes, c => c is JobsChanged { Ids.Count: 3 });
        Assert.Equal("Verificação diária: 8 checadas, 3 inativas removidas, 2 ativas, 3 inconclusivas mantidas.", WorthReverify.FormatSummary(summary));
        Assert.All(http.Requests, r => Assert.Equal(WorthReverify.UserAgent, r.UserAgent));
    }

    [Fact]
    public async Task A_network_failure_keeps_the_job_and_cancellation_aborts_the_pass()
    {
        using var t = new TempDb();
        AddWorth(t, "https://empresa.test/vaga/1");
        var failing = new WorthReverify(t.Db, new HttpClient(new ThrowingHandler()));
        Assert.Equal(new ReverifySummary(1, 0, 0, 1, false), await failing.ReverifyAsync());

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var aborted = await new WorthReverify(t.Db, new HttpClient(new RouteHandler())).ReverifyAsync(cancelled.Token);
        Assert.Equal(new ReverifySummary(0, 0, 0, 0, true), aborted);
        Assert.EndsWith("Interrompida por cancelamento.", WorthReverify.FormatSummary(aborted));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("sem rede");
    }

    [Fact]
    public async Task The_queue_handler_logs_the_summary()
    {
        using var t = new TempDb();
        AddWorth(t, "https://empresa.test/vaga/1");
        var http = new RouteHandler().On("GET", "empresa.test", "Apply now");
        var result = await new WorthReverify(t.Db, new HttpClient(http)).Handler(new QueueJob(1, "worth_reverify", new JsonObject(), _ => { }), CancellationToken.None);
        Assert.Equal("Verificação diária: 1 checadas, 0 inativas removidas, 1 ativas, 0 inconclusivas mantidas.", result);
        Assert.Equal(result, t.Scalar<string>("SELECT message FROM event_logs WHERE source='worth-check'"));
    }
}

public class DailySchedulerTests
{
    private static DateTime At(string text) => DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static (DailyScheduler Scheduler, Func<int> Syncs, Func<int> Purges) Make(TempDb t, Func<DateTime> now, int purged = 0)
    {
        int syncs = 0, purges = 0;
        var scheduler = new DailyScheduler(t.Db, _ => { syncs++; return Task.CompletedTask; }, () => { purges++; return purged; }, now);
        return (scheduler, () => syncs, () => purges);
    }

    [Fact]
    public async Task Syncs_the_portfolio_once_a_day_and_waits_until_three_to_queue_the_check()
    {
        using var t = new TempDb();
        t.AddJob(status: "worth");
        var clock = At("2026-11-23T02:30:00");
        var (scheduler, syncs, purges) = Make(t, () => clock);
        await scheduler.TickAsync();
        await scheduler.TickAsync();
        Assert.Equal(1, syncs());
        Assert.Equal(0, purges());
        Assert.Equal(0, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs"));

        clock = At("2026-11-23T03:00:05");
        await scheduler.TickAsync();
        Assert.Equal(1, syncs());
        Assert.Equal(1, purges());
        Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs WHERE kind='worth_reverify'"));
        Assert.Equal("2026-11-23", t.Scalar<string>("SELECT value FROM settings WHERE key='worth_reverify_last_daily'"));
        Assert.Contains("\"scheduled_date\": \"2026-11-23\"", t.Scalar<string>("SELECT payload FROM queue_jobs"));
        Assert.Matches(@"Verificação diária enfileirada \(fila #\d+, 1 vaga\(s\)\)\.", t.Scalar<string>("SELECT message FROM event_logs WHERE source='worth-check'"));

        await scheduler.TickAsync(); // o mesmo dia não enfileira de novo
        Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs"));

        clock = At("2026-11-24T04:00:00"); // novo dia: sincroniza de novo e enfileira outra
        await scheduler.TickAsync();
        Assert.Equal(2, syncs());
        Assert.Equal(2, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs"));
    }

    [Fact]
    public async Task Another_instance_that_already_claimed_the_day_prevents_a_second_check()
    {
        using var t = new TempDb();
        t.AddJob(status: "worth");
        using (var c = t.Db.Open()) c.Execute("UPDATE settings SET value='2026-11-23' WHERE key='worth_reverify_last_daily'");
        var (scheduler, _, purges) = Make(t, () => At("2026-11-23T10:00:00"));
        await scheduler.TickAsync();
        Assert.Equal(0, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs"));
        Assert.Equal(0, purges());
    }

    [Fact]
    public async Task Without_worth_jobs_it_only_logs_and_the_retention_report_is_written()
    {
        using var t = new TempDb();
        var (scheduler, _, _) = Make(t, () => At("2026-11-23T05:00:00"), purged: 7);
        await scheduler.TickAsync();
        Assert.Equal(0, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs"));
        var logs = t.Db.Open().Query<string>("SELECT message FROM event_logs ORDER BY id").ToList();
        Assert.Contains("Retenção: 7 item(ns) terminado(s) há mais de 14 dias removido(s).", logs);
        Assert.Contains("Verificação diária: nenhuma vaga para checar.", logs);
    }

    [Fact]
    public async Task A_failure_while_queueing_releases_the_claim_so_the_next_pass_retries()
    {
        using var t = new TempDb();
        t.AddJob(status: "worth");
        using (var c = t.Db.Open()) c.Execute("CREATE TRIGGER fail_enqueue BEFORE INSERT ON queue_jobs BEGIN SELECT RAISE(ABORT, 'banco travado'); END");
        var (scheduler, _, _) = Make(t, () => At("2026-11-23T05:00:00"));
        await Assert.ThrowsAnyAsync<Exception>(() => scheduler.TickAsync());
        Assert.Equal("", t.Scalar<string>("SELECT value FROM settings WHERE key='worth_reverify_last_daily'"));
        using (var c = t.Db.Open()) c.Execute("DROP TRIGGER fail_enqueue");
        await scheduler.TickAsync();
        Assert.Equal(1, t.Scalar<int>("SELECT COUNT(*) FROM queue_jobs"));
    }
}
