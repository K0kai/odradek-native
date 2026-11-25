using System.Text.Json.Nodes;
using Odradek.Core.Collector;

namespace Odradek.Core.Tests;

/// <summary>Paridade com o planejador de buscas do Apify/LinkedIn do <c>app.py</c> (cenários gerados e calculados pelo Python).</summary>
public class LinkedInSearchTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "search_cases.json")))!;
    private static readonly JsonObject FilterMain = JsonNode.Parse("""{"id":"main-abc","name":"Software Engineer","slot":"main","enabled":true,"keywords":["Software Engineer","Engenheiro de Software"],"experience_levels":[3,4],"reason":"r","source":"ai"}""")!.AsObject();
    private static readonly JsonObject FilterSec = JsonNode.Parse("""{"id":"sec-def","name":"Analista de Suporte","slot":"secondary","enabled":true,"keywords":["IT Support & Helpdesk"],"experience_levels":[],"reason":"r","source":"ai","track":"transferable"}""")!.AsObject();

    /// <summary>Config dos cenários do Python, que sempre buscavam em "Worldwide": o toggle entra ligado para o escopo continuar igual.</summary>
    private static Dictionary<string, string> Cfg(string name) => DefaultCfg(name).Concat([KeyValuePair.Create("linkedin_search_worldwide", "1")]).ToDictionary();

    /// <summary>Config do cenário como está, sem o toggle "Worldwide": o padrão do app.</summary>
    private static Dictionary<string, string> DefaultCfg(string name) => Cases["cfgs"]![name]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());

    private static JsonArray ActorsFromFixture()
    {
        // os mesmos actors do gerador, lidos do primeiro plano ("main_only" contém todos)
        return JsonNode.Parse("""
[{"id":"curious_coder~linkedin-jobs-scraper","label":"LinkedIn Jobs","enabled":true,"input_mode":"linkedin_search","count":25,"input_template":null},
 {"id":"misceres~indeed-scraper","label":"Indeed","enabled":true,"input_mode":"custom","count":30,"input_template":{"position":"{{keyword}}","country":"{{location}}","maxItems":"{{count}}"}},
 {"id":"other~fixed","label":"Fixed","enabled":true,"input_mode":"custom","count":10,"input_template":{"q":"dev"}},
 {"id":"off~actor","label":"Off","enabled":false,"input_mode":"linkedin_search","count":5,"input_template":null}]
""")!.AsArray();
    }

    [Fact]
    public void Quote_plus_matches_python()
    {
        foreach (var item in Cases["quote_plus"]!.AsArray().OfType<JsonObject>())
            Assert.Equal(item["quoted"]!.GetValue<string>(), LinkedInSearch.QuotePlus(item["text"]!.GetValue<string>()));
    }

    [Fact]
    public void Max_age_and_recency_phases_match_python()
    {
        foreach (var item in Cases["max_age"]!.AsArray().OfType<JsonObject>())
        {
            var cfg = item.ContainsKey("raw") ? new Dictionary<string, string> { ["linkedin_max_age_days"] = item["raw"]!.GetValue<string>() } : Cfg(item["cfg"]!.GetValue<string>());
            var days = LinkedInSearch.MaxAgeDays(cfg);
            Assert.Equal(item["days"]!.GetValue<int>(), days);
            Assert.Equal(item["phases"]!.AsArray().Select(p => p!.GetValue<int>()), LinkedInSearch.RecencyPhases(days));
        }
    }

    [Fact]
    public void Search_scope_matches_python_for_every_config()
    {
        foreach (var name in Cases["cfgs"]!.AsObject().Select(kv => kv.Key))
        {
            var expected = Cases["scope"]![name]!.AsArray().Select(s => (s!["location"]!.GetValue<string>(), s["f_wt"]!.GetValue<string>(), s["label"]!.GetValue<string>())).ToList();
            var actual = LinkedInSearch.SearchScope(Cfg(name)).Select(s => (s.Location, s.FWt, s.Label)).ToList();
            Assert.Equal(expected, actual);
            Assert.Equal(Cases["custom_locations"]![name]!.AsArray().Select(s => s!.GetValue<string>()), LinkedInSearch.CustomSearchLocations(Cfg(name)));
        }
    }

    [Fact]
    public void Search_urls_match_python_for_main_and_secondary_filters()
    {
        foreach (var item in Cases["urls"]!.AsArray().OfType<JsonObject>())
        {
            var scope = LinkedInSearch.SearchScope(Cfg(item["cfg"]!.GetValue<string>()));
            var days = item["days"]!.GetValue<int>();
            Assert.Equal(item["main"]!.AsArray().Select(u => u!.GetValue<string>()), LinkedInSearch.BuildSearchUrls(FilterMain, scope, days));
            Assert.Equal(item["sec"]!.AsArray().Select(u => u!.GetValue<string>()), LinkedInSearch.BuildSearchUrls(FilterSec, scope, days));
        }
    }

    [Fact]
    public void Worldwide_is_only_searched_when_the_toggle_is_on_and_never_leaves_the_scope_empty()
    {
        var off = LinkedInSearch.SearchScope(DefaultCfg("br_bh"));
        Assert.Equal(["Brazil", "Belo Horizonte", "Portugal"], off.Select(s => s.Location));
        Assert.DoesNotContain(off, s => s.Location == "Worldwide");
        var on = LinkedInSearch.SearchScope(Cfg("br_bh"));
        Assert.Equal("Worldwide", on[^1].Location);
        Assert.Equal(off.Select(s => s.Location), on.Where(s => s.Location != "Worldwide").Select(s => s.Location));

        var bare = LinkedInSearch.SearchScope(new Dictionary<string, string>()); // nada configurado: ainda assim há uma busca
        Assert.Equal(["Worldwide"], bare.Select(s => s.Location));
        Assert.Equal([("Worldwide", "2")], LinkedInSearch.SearchScope(new Dictionary<string, string>
            { ["linkedin_search_worldwide"] = "0" }).Select(s => (s.Location, s.FWt)));
        Assert.False(LinkedInSearch.WorldwideEnabled(new Dictionary<string, string>()));
        Assert.True(LinkedInSearch.WorldwideEnabled(new Dictionary<string, string> { ["linkedin_search_worldwide"] = " 1 " }));
    }

    [Fact]
    public void Search_urls_carry_the_country_geoId_and_sort_by_relevance()
    {
        var scope = LinkedInSearch.SearchScope(Cfg("br_bh"));
        var urls = LinkedInSearch.BuildSearchUrls(FilterSec, scope, 1);
        Assert.All(urls, url => Assert.Contains("&sortBy=R", url));
        Assert.DoesNotContain(urls, url => url.Contains("sortBy=DD"));
        string For(string location) => urls.First(url => url.Contains($"&location={location}&", StringComparison.Ordinal));
        Assert.Contains("&location=Brazil&geoId=106057199&position=1", For("Brazil"));
        Assert.Contains("&location=Portugal&geoId=100364837&position=1", For("Portugal"));
        Assert.Contains("&location=Worldwide&geoId=92000000&position=1", For("Worldwide"));
        Assert.DoesNotContain("geoId", For("Belo+Horizonte")); // cidade: só o texto do local
        Assert.Contains("&f_WT=2", For("Brazil"));

        foreach (var (name, id) in new[] { ("Brasil", "106057199"), ("UK", "101165590"), ("United States", "103644278"), ("alemanha", "101282230"), ("Reino Unido", "101165590") })
            Assert.Equal(id, Geo.LinkedInGeoId(name));
        foreach (var name in new[] { "Belo Horizonte", "remote", "India", "", null })
            Assert.Equal("", Geo.LinkedInGeoId(name));
    }

    [Fact]
    public void Count_and_work_model_splits_match_python()
    {
        foreach (var item in Cases["split_count"]!.AsArray().OfType<JsonObject>())
        {
            var (b, s) = (item["base"]!.GetValue<int>(), item["share"]!.GetValue<int>());
            Assert.Equal(item["two"]!.AsArray().Select(x => x!.GetValue<int>()), LinkedInSearch.SplitApifyCount(b, 2, s));
            Assert.Equal(item["one"]!.AsArray().Select(x => x!.GetValue<int>()), LinkedInSearch.SplitApifyCount(b, 1, s));
            Assert.Empty(LinkedInSearch.SplitApifyCount(b, 0, s));
        }
        foreach (var item in Cases["split_urls"]!.AsArray().OfType<JsonObject>())
        {
            var urls = item["urls"]!.AsArray().Select(u => u!.GetValue<string>()).ToList();
            var expected = item["result"]!.AsArray().Select(r => (r![0]!.AsArray().Select(u => u!.GetValue<string>()).ToList(), r[1]!.GetValue<int>(), r[2]!.GetValue<bool>())).ToList();
            var actual = LinkedInSearch.SplitUrlsByWorkModel(urls, item["count"]!.GetValue<int>()).Select(p => (p.Urls, p.Count, p.Remote)).ToList();
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Item1, actual[i].Urls);
                Assert.Equal((expected[i].Item2, expected[i].Item3), (actual[i].Count, actual[i].Remote));
            }
        }
    }

    [Fact]
    public void Apify_run_plans_match_python_for_every_config_and_filter_combination()
    {
        var actors = ActorsFromFixture().OfType<JsonObject>().ToList();
        foreach (var item in Cases["plans"]!.AsArray().OfType<JsonObject>())
        {
            var cfg = Cfg(item["cfg"]!.GetValue<string>());
            var scope = LinkedInSearch.SearchScope(cfg);
            var (main, sec, used) = item["case"]!.GetValue<string>() switch
            {
                "main_only" => (FilterMain, (JsonObject?)null, actors),
                "main_sec" => (FilterMain, FilterSec, actors),
                "none" => (null, null, actors),
                "sec_disabled" => (FilterMain, (JsonObject)new JsonObject(FilterSec.Select(kv => KeyValuePair.Create(kv.Key, kv.Key == "enabled" ? JsonValue.Create(false) : kv.Value?.DeepClone()))), actors),
                "linkedin_only" => (FilterMain, FilterSec, actors.Take(1).ToList()),
                _ => (FilterMain, FilterSec, actors),
            };
            var noCustom = item["case"]!.GetValue<string>() == "no_custom_locations_share90";
            var plan = LinkedInSearch.PlanApifyRuns(used, main, sec, scope, mainShare: noCustom ? 90 : 70, maxAgeDays: item["max_age"]!.GetValue<int>(),
                customLocations: noCustom ? null : LinkedInSearch.CustomSearchLocations(cfg), fallbackKeywords: LinkedInSearch.Terms(cfg.GetValueOrDefault("keywords")));
            var actual = new JsonArray(plan.Select(run => (JsonNode?)new JsonObject
            {
                ["actor"] = run.Actor.DeepClone(), ["filter_id"] = run.Filter?["id"]?.DeepClone(),
                ["urls"] = run.Urls is null ? null : new JsonArray(run.Urls.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()),
            }).ToArray());
            var diff = JsonCompare.Diff(item["plan"], actual);
            Assert.True(diff is null, $"{item["cfg"]}/{item["case"]}: {diff}");
        }
    }

    [Fact]
    public void Plans_cover_the_interesting_shapes()
    {
        var plans = Cases["plans"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Contains(plans, p => p["plan"]!.AsArray().Count > 10);
        Assert.Contains(plans, p => p["plan"]!.AsArray().Any(r => r!["actor"]!["_ctx"] is not null)); // actor custom com modelo
        Assert.Contains(plans, p => p["plan"]!.AsArray().Any(r => r!["actor"]!["_search_remote"] is { } v && v.GetValue<bool>()));
        Assert.Contains(plans, p => p["plan"]!.AsArray().Count == 1); // só o actor fixo, sem filtro nem palavras-chave
    }

    [Fact]
    public void Run_inputs_match_python_including_template_substitution_and_errors()
    {
        foreach (var item in Cases["run_inputs"]!.AsArray().OfType<JsonObject>())
        {
            var cfg = Cfg(item["cfg"]!.GetValue<string>());
            var filter = item["filter"] as JsonObject;
            var urls = item["urls"] is JsonArray a ? a.Select(u => u!.GetValue<string>()).ToList() : null;
            var expected = item["input"]!.AsObject();
            JsonObject actual;
            try { actual = ApifyActors.BuildRunInput((JsonObject)item["actor"]!.DeepClone(), cfg, filter, urls); }
            catch (Odradek.Core.Actions.ActionException ex) { actual = new JsonObject { ["__error__"] = ex.Message }; }
            var diff = JsonCompare.Diff(expected, actual);
            Assert.True(diff is null, $"{item["actor"]!["label"]}: {diff}");
        }
    }

    [Fact]
    public void Actors_load_with_the_same_defaults_ids_and_limits()
    {
        Assert.Equal("curious_coder~linkedin-jobs-scraper", ApifyActors.Load(new Dictionary<string, string>())[0]["id"]!.GetValue<string>());
        var cfg = new Dictionary<string, string>
        {
            ["apify_job_count"] = "40",
            ["apify_actors_json"] = """[{"id":"owner/name","count":500},{"id":"a~b","label":"B","enabled":false,"input_mode":"custom","input":{"q":"x"}},{"id":"c~d","count":"abc"},{"id":"e~f","input_mode":"weird","input_template":[1]}]""",
        };
        var actors = ApifyActors.Load(cfg);
        Assert.Equal(["owner~name", "a~b", "c~d", "e~f"], actors.Select(x => x["id"]!.GetValue<string>()));
        Assert.Equal([100, 40, 25, 40], actors.Select(x => x["count"]!.GetValue<int>()));
        Assert.Equal(["linkedin_search", "custom", "linkedin_search", "custom"], actors.Select(x => x["input_mode"]!.GetValue<string>()));
        Assert.False(actors[1]["enabled"]!.GetValue<bool>());
        Assert.Equal("owner~name", actors[0]["label"]!.GetValue<string>());
        Assert.Equal("x", actors[1]["input_template"]!["q"]!.GetValue<string>());
        Assert.Throws<Odradek.Core.Actions.ActionException>(() => ApifyActors.Load(new Dictionary<string, string> { ["apify_actors_json"] = "{}" }));
        Assert.Throws<Odradek.Core.Actions.ActionException>(() => ApifyActors.Load(new Dictionary<string, string> { ["apify_actors_json"] = """[{"label":"sem id"}]""" }));
    }

    [Fact]
    public void Numbers_created_in_code_count_as_truthy_and_drive_the_plan()
    {
        Assert.True(Text.Truthy(JsonValue.Create(5)));
        Assert.False(Text.Truthy(JsonValue.Create(0)));
        Assert.True(Text.Truthy(JsonValue.Create(0.5)));
        var actors = ApifyActors.Load(new Dictionary<string, string> { ["apify_actors_json"] = """[{"id":"a~b","count":10}]""" }); // Load cria "count" como int do C#
        var plan = LinkedInSearch.PlanApifyRuns(actors, FilterMain, null, LinkedInSearch.SearchScope(Cfg("br_bh")));
        Assert.NotEmpty(plan);
        Assert.All(plan, run => Assert.True(run.Actor["count"]!.GetValue<int>() is >= 1 and <= 10));
    }
}
