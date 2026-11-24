using System.Text.Json.Nodes;
using Odradek.Core.Actions;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.Core.Tests;

public class QuotaServiceTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "quota_cases.json")))!;
    private const string Now = "2026-06-01T12:00:00+00:00";

    [Fact]
    public void Parsed_limits_and_free_tier_are_byte_identical_to_python_json()
    {
        foreach (var item in Cases["apify"]!.AsArray().OfType<JsonObject>())
        {
            var actual = PyJson.Serialize(QuotaService.ParseApifyLimits(item["payload"]!.DeepClone(), item["local"]!.GetValue<double>(), Now));
            Assert.True(item["result"]!.GetValue<string>() == actual, $"{item["payload"]}\n{item["result"]}\n{actual}");
        }
        foreach (var item in Cases["tokenharbor"]!.AsArray().OfType<JsonObject>())
        {
            var actual = PyJson.Serialize(QuotaService.ParseTokenHarborFreeTier(item["payload"]!.DeepClone(), Now));
            Assert.True(item["result"]!.GetValue<string>() == actual, $"{item["payload"]}\n{item["result"]}\n{actual}");
        }
    }

    private static (TempDb T, RouteHandler Http, QuotaService Service) Setup(Action<RouteHandler> routes, string apifyToken = "APIFY", string harborKey = "HARBOR")
    {
        var t = new TempDb();
        var http = new RouteHandler();
        routes(http);
        var service = new QuotaService(t.Db, new ApifyClient(new HttpClient(http)), name => name switch { "apify_token" => apifyToken, "tokenharbor" => harborKey, _ => "" });
        return (t, http, service);
    }

    private const string Limits = """{"data":{"limits":{"maxMonthlyUsageUsd":39},"current":{"monthlyUsageUsd":2.5},"monthlyUsageCycle":{"startAt":"2026-05-10T00:00:00.000Z","endAt":"2026-06-09T23:59:59.999Z"}}}""";

    [Fact]
    public async Task Apify_snapshot_is_saved_with_the_collector_usage_and_announced()
    {
        var (t, http, service) = Setup(h => h.On("GET", "users/me/limits", Limits));
        using var _ = t;
        var snapshot = await service.RefreshApifyAsync();
        Assert.Equal(2.5, snapshot["used_usd"]!.GetValue<double>());
        var settings = new SettingsStore(t.Db);
        Assert.Equal("2.5000", settings.Get("apify_last_usage_usd"));
        Assert.Equal("10/05/2026 a 09/06/2026", settings.Get("apify_last_usage_cycle"));
        Assert.Equal(PyJson.Serialize(snapshot), settings.Get(QuotaService.ApifySnapshotKey));
        Assert.Contains(t.Changes, c => c is QuotaChanged);
        Assert.Equal("APIFY", http.Requests.Single().Bearer);
    }

    [Fact]
    public async Task A_recent_snapshot_is_reused_until_it_is_older_than_the_minimum_age()
    {
        var (t, http, service) = Setup(h => h.On("GET", "users/me/limits", Limits));
        using var _ = t;
        await service.RefreshApifyAsync();
        await service.RefreshApifyAsync(minAgeSeconds: 15);
        await service.RefreshApifyAsync(minAgeSeconds: 300);
        Assert.Single(http.Requests);
        await service.RefreshApifyAsync(); // sem idade mínima: consulta de novo
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(2, (await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.RefreshApifyAsync(minAgeSeconds: 300)))).Length - 1);
        Assert.Equal(2, http.Requests.Count); // as chamadas simultâneas encontram o snapshot fresco
    }

    [Fact]
    public async Task Missing_token_and_api_failures_become_snapshots_with_an_error_and_are_retried()
    {
        var (t, http, service) = Setup(h => h.OnStatus("GET", "users/me/limits", (500, "falhou"), (200, Limits)));
        using var _ = t;
        var failed = await service.RefreshApifyAsync(minAgeSeconds: 300);
        Assert.Equal("Falha ao consultar Apify: HTTP 500: falhou", failed["error"]!.GetValue<string>());
        Assert.Equal("—", failed["cycle_label"]!.GetValue<string>());
        var again = await service.RefreshApifyAsync(minAgeSeconds: 300); // snapshot com erro não vale como cache
        Assert.Equal("", again["error"]!.GetValue<string>());
        Assert.Equal(2, http.Requests.Count);

        var (t2, http2, noToken) = Setup(_ => { }, apifyToken: "");
        using (t2)
        {
            var snapshot = await noToken.RefreshApifyAsync();
            Assert.Equal("Token Apify não configurado (IA & integrações).", snapshot["error"]!.GetValue<string>());
            Assert.Empty(http2.Requests);
            Assert.Equal(PyJson.Serialize(snapshot), new SettingsStore(t2.Db).Get(QuotaService.ApifySnapshotKey));
            Assert.Equal(snapshot["error"]!.GetValue<string>(), (await noToken.FreshApifyAsync(collectorRunning: true))["error"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task TokenHarbor_snapshot_is_cached_for_a_minute_unless_forced_and_never_leaks_the_key()
    {
        var (t, http, service) = Setup(h => h.On("GET", "tokenharbor.ai/api/cli/free-tier", """{"plan_name":"Free","used_pct":42,"models":["a:free"]}"""));
        using var _ = t;
        var first = await service.TokenHarborSnapshotAsync();
        Assert.Equal(42, first["used_pct"]!.GetValue<int>());
        Assert.Equal("HARBOR", http.Requests.Single().Bearer);
        await service.TokenHarborSnapshotAsync();
        Assert.Single(http.Requests);
        await service.TokenHarborSnapshotAsync(force: true);
        Assert.Equal(2, http.Requests.Count);

        var (t2, _, leaky) = Setup(h => h.OnStatus("GET", "tokenharbor.ai", (401, "chave HARBOR-SEGREDO inválida")), harborKey: "HARBOR-SEGREDO");
        using (t2)
        {
            var snapshot = await leaky.TokenHarborSnapshotAsync();
            Assert.Equal("Falha ao consultar o TokenHarbor: HTTP 401: chave [chave omitida] inválida", snapshot["error"]!.GetValue<string>());
            Assert.DoesNotContain("SEGREDO", snapshot.ToJsonString());
        }

        var (t3, http3, none) = Setup(_ => { }, harborKey: "");
        using (t3)
        {
            Assert.False((await none.TokenHarborSnapshotAsync())["configured"]!.GetValue<bool>());
            Assert.Empty(http3.Requests);
        }
    }
}
