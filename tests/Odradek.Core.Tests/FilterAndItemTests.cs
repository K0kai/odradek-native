using System.Text.Json.Nodes;
using Odradek.Core.Actions;
using Odradek.Core.Collector;

namespace Odradek.Core.Tests;

/// <summary>Paridade com os filtros salvos, a normalização dos itens do Apify e a detecção de idioma do <c>app.py</c>.</summary>
public class FilterAndItemTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "filter_item_cases.json")))!;

    [Fact]
    public void Normalized_filters_are_byte_identical_to_python_json()
    {
        foreach (var item in Cases["normalize"]!.AsArray().OfType<JsonObject>())
        {
            var actual = PyJson.Serialize(LinkedInFilters.Normalize(item["raw"]?.DeepClone(), item["slot"]!.GetValue<string>()));
            Assert.True(item["result"]!.GetValue<string>() == actual, $"{item["raw"]?.ToJsonString()} [{item["slot"]}]\nesperado: {item["result"]}\nobtido:   {actual}");
        }
    }

    [Fact]
    public void Form_validation_matches_python_results_and_messages()
    {
        foreach (var item in Cases["validate"]!.AsArray().OfType<JsonObject>())
        {
            var form = item["form"]!.GetValue<string>();
            var slot = item["slot"]!.GetValue<string>();
            if (item["error"] is { } expectedError)
            {
                var ex = Assert.Throws<ActionException>(() => LinkedInFilters.ValidateForm(form, slot));
                var message = ex.Message.StartsWith("JSON do filtro inválido", StringComparison.Ordinal) ? "JSON inválido" : ex.Message;
                Assert.True(expectedError.GetValue<string>() == message, $"'{form}' [{slot}]");
            }
            else Assert.True(item["result"]!.GetValue<string>() == LinkedInFilters.ValidateForm(form, slot), $"'{form}' [{slot}]");
        }
    }

    [Fact]
    public void Saved_filters_load_like_python_and_warn_when_invalid()
    {
        foreach (var item in Cases["load"]!.AsArray().OfType<JsonObject>())
        {
            var slot = item["slot"]!.GetValue<string>();
            var logs = new List<string>();
            var cfg = new Dictionary<string, string> { [LinkedInFilters.KeyFor(slot)] = item["raw"]!.GetValue<string>() };
            var loaded = LinkedInFilters.Load(cfg, slot, (level, message) => logs.Add($"{level}:{message}"));
            Assert.True(item["result"]!.GetValue<string>() == PyJson.Serialize(loaded), $"'{item["raw"]}' [{slot}]");
            Assert.Equal(loaded is null && item["raw"]!.GetValue<string>().Trim().Length > 0 ? 1 : 0, logs.Count);
        }
    }

    private static JsonNode? Posted(JsonNode? expected) => expected switch
    {
        null => null,
        JsonObject or JsonArray => null, // o Python quebraria ao gravar um dict no SQLite; aqui a coluna fica nula
        _ => JsonValue.Create(Text.PyStrRaw(expected)), // número vira o texto que o SQLite guarda numa coluna TEXT
    };

    [Fact]
    public void Apify_items_normalize_like_python()
    {
        foreach (var scenario in Cases["items"]!.AsArray().OfType<JsonObject>())
        {
            var label = scenario["label"]!.GetValue<string>();
            var preferred = scenario["preferred"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
            var logs = new List<string>();
            var actual = ApifyItems.Normalize(scenario["items"]!.AsArray().Select(i => i?.DeepClone()).ToList(), label, scenario["max_age"]!.GetValue<int>(), preferred, (l, m) => logs.Add(m));
            var expected = new JsonArray(scenario["result"]!.AsArray().Select(r =>
            {
                var copy = (JsonObject)r!.DeepClone();
                copy["posted_at"] = Posted(r["posted_at"]);
                return (JsonNode?)copy;
            }).ToArray());
            var shaped = new JsonArray(actual.Select(j => (JsonNode?)new JsonObject
            {
                ["source"] = j.Source, ["source_id"] = j.SourceId, ["title"] = j.Title, ["company"] = j.Company, ["location"] = j.Location,
                ["description"] = j.Description, ["url"] = j.Url, ["posted_at"] = j.PostedAt,
            }).ToArray());
            var diff = JsonCompare.Diff(expected, shaped);
            Assert.True(diff is null, $"{label}/{scenario["max_age"]}: {diff}");
            if (actual.Count < scenario["items"]!.AsArray().Count) Assert.Contains(logs, m => m.Contains("descartada(s) no filtro"));
        }
    }

    [Fact]
    public void Linkedin_results_are_ordered_by_bias_then_recency_and_other_sources_keep_the_order()
    {
        var scenario = Cases["items"]!.AsArray().OfType<JsonObject>().First(s => s["label"]!.GetValue<string>() == "LinkedIn Jobs" && s["max_age"]!.GetValue<int>() == 7);
        var titles = scenario["result"]!.AsArray().Select(r => r!["title"]!.GetValue<string>()).ToList();
        Assert.True(titles.IndexOf("A") >= 0 && titles.IndexOf("Z") >= 0);
        Assert.DoesNotContain("Velha", titles); // mais antiga que a janela
        Assert.DoesNotContain("Fechada", titles);
    }

    [Theory]
    [InlineData("This job is no longer available", true)]
    [InlineData("Esta vaga não está mais aceitando candidaturas", true)]
    [InlineData("inscrições encerradas", true)]
    [InlineData("We are hiring a developer", false)]
    public void Closed_text_is_recognised(string text, bool closed) => Assert.Equal(closed, ApifyItems.TextIndicatesClosed(text));

    [Fact]
    public void Closed_flags_and_states_are_recognised()
    {
        Assert.True(ApifyItems.IndicatesClosed((JsonObject)JsonNode.Parse("""{"title":"x","isClosed":"Yes"}""")!));
        Assert.True(ApifyItems.IndicatesClosed((JsonObject)JsonNode.Parse("""{"title":"x","expired":true}""")!));
        Assert.True(ApifyItems.IndicatesClosed((JsonObject)JsonNode.Parse("""{"title":"x","jobStatus":" Archived "}""")!));
        Assert.False(ApifyItems.IndicatesClosed((JsonObject)JsonNode.Parse("""{"title":"x","closed":false,"jobState":"open"}""")!));
    }

    [Fact]
    public void Language_detection_matches_the_python_word_list_fallback()
    {
        foreach (var item in Cases["language"]!.AsArray().OfType<JsonObject>())
        {
            var (language, confidence) = LanguageDetector.Detect(item["text"]!.GetValue<string>());
            Assert.Equal(item["language"]!.GetValue<string>(), language);
            Assert.Equal(item["confidence"]!.GetValue<double>(), confidence, 9);
        }
    }

    [Fact]
    public void Indian_jobs_are_dropped_before_collection()
    {
        var batch = new List<CollectedJob>
        {
            new("s", "1", "Dev", "A", "Bengaluru, India", "x", "https://x/1", null),
            new("s", "2", "Dev", "B", "Brazil", "x", "https://x/2", null),
        };
        var (kept, dropped) = ApifyItems.DropExcludedRegion(batch);
        Assert.Equal(1, dropped);
        Assert.Equal("2", kept.Single().SourceId);
    }
}
