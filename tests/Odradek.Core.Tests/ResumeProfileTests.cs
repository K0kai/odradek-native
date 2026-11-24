using System.Text.Json.Nodes;
using Odradek.Core.Candidate;

namespace Odradek.Core.Tests;

/// <summary>Paridade com <c>candidate/resume_profile.py</c>: os casos vêm de <c>tests/fixtures/resume_profile_cases.json</c>, gerado pelo código Python com texto sintético.</summary>
public class ResumeProfileTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "resume_profile_cases.json")))!;
    private static readonly DateOnly Today = DateOnly.Parse((string)Cases["today"]!);
    private static string Text(string key) => Cases[key]!.GetValue<string>();
    private static JsonObject Obj(string key) => (JsonObject)Cases[key]!;

    private static void SameJson(JsonNode? expected, JsonNode? actual, string label)
    {
        if (JsonNode.DeepEquals(expected, actual)) return;
        Assert.Fail($"{label} difere do Python.\nPython: {expected?.ToJsonString()}\nC#:     {actual?.ToJsonString()}");
    }

    [Fact]
    public void The_json_comparer_really_detects_differences()
    {
        Assert.False(JsonNode.DeepEquals(Obj("profile_pt"), Obj("profile_en")));
        var changed = (JsonObject)Obj("profile_pt").DeepClone();
        changed["roles"]![0]!["start"] = "2021-02";
        Assert.False(JsonNode.DeepEquals(Obj("profile_pt"), changed));
        Assert.True(JsonNode.DeepEquals(Obj("profile_pt"), Obj("profile_pt").DeepClone()));
    }

    [Fact]
    public void GroundProfile_matches_python_for_portuguese_and_english_resumes()
    {
        SameJson(Obj("profile_pt"), ResumeProfile.GroundProfile((JsonObject)Obj("raw_pt").DeepClone(), Text("resume_pt"), Today), "perfil PT");
        SameJson(Obj("profile_en"), ResumeProfile.GroundProfile((JsonObject)Obj("raw_en").DeepClone(), Text("resume_en"), Today), "perfil EN");
    }

    [Fact]
    public void GroundProfile_keeps_the_python_key_order_the_database_json_depends_on()
    {
        var profile = ResumeProfile.GroundProfile((JsonObject)Obj("raw_pt").DeepClone(), Text("resume_pt"), Today);
        Assert.Equal(Obj("profile_pt").Select(kv => kv.Key), profile.Select(kv => kv.Key));
        Assert.Equal(((JsonObject)Obj("profile_pt")["derived"]!).Select(kv => kv.Key), ((JsonObject)profile["derived"]!).Select(kv => kv.Key));
    }

    [Fact]
    public void MergeProfiles_matches_python_including_portfolio_enrichment_and_reassigned_ids()
    {
        var merged = ResumeProfile.MergeProfiles([Obj("profile_pt"), Obj("profile_en"), Obj("portfolio")], Today);
        SameJson(Obj("merged"), merged, "perfil fundido");
        Assert.Single(Obj("merged")["projects"]!.AsArray()); // o projeto do portfólio enriqueceu o do PDF em vez de duplicar
    }

    [Fact]
    public void MergeProfiles_does_not_mutate_its_inputs()
    {
        var before = Obj("profile_pt").ToJsonString();
        ResumeProfile.MergeProfiles([Obj("profile_pt"), Obj("profile_en")], Today);
        Assert.Equal(before, Obj("profile_pt").ToJsonString());
    }

    [Fact]
    public void CompactProfileText_matches_python_at_each_size()
    {
        Assert.Equal(Text("compact_pt"), ResumeProfile.CompactProfileText(Obj("profile_pt"), 6000));
        Assert.Equal(Text("compact_merged"), ResumeProfile.CompactProfileText(Obj("merged"), 9500));
        Assert.Equal(Text("compact_short"), ResumeProfile.CompactProfileText(Obj("merged"), 400));
    }

    [Fact]
    public void EvidenceIndex_matches_python()
    {
        var index = ResumeProfile.EvidenceIndex(Obj("merged"));
        var expected = Obj("evidence_index");
        Assert.Equal(expected.Select(kv => kv.Key).OrderBy(k => k), index.Keys.OrderBy(k => k));
        foreach (var (id, item) in expected)
        {
            Assert.Equal(item!["kind"]!.GetValue<string>(), index[id].Kind);
            Assert.Equal(item["text"]!.GetValue<string>(), index[id].Text);
            Assert.Equal(item["quotes"]!.AsArray().Select(q => q!.GetValue<string>()), index[id].Quotes);
        }
    }

    [Fact]
    public void QuoteInText_agrees_with_python_on_exact_fuzzy_short_and_unrelated_quotes()
    {
        var pt = NormalizedText.Of(Text("resume_pt"));
        var en = NormalizedText.Of(Text("resume_en"));
        foreach (var probe in Cases["quote_probes"]!.AsArray().OfType<JsonObject>())
        {
            var quote = probe["quote"]!.GetValue<string>();
            Assert.True(probe["pt"]!.GetValue<bool>() == ResumeProfile.QuoteInText(quote, pt), $"PT: «{quote}»");
            Assert.True(probe["en"]!.GetValue<bool>() == ResumeProfile.QuoteInText(quote, en), $"EN: «{quote}»");
        }
    }

    [Fact]
    public void SplitSections_matches_python_offsets()
    {
        foreach (var (key, text) in new[] { ("sections_pt", Text("resume_pt")), ("sections_en", Text("resume_en")) })
        {
            var actual = ResumeProfile.SplitSections(text);
            var expected = Cases[key]!.AsArray().OfType<JsonObject>().ToList();
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i]["title"]!.GetValue<string>(), actual[i].Title);
                Assert.Equal(expected[i]["kind"]!.GetValue<string>(), actual[i].Kind);
                Assert.Equal(expected[i]["start"]!.GetValue<int>(), actual[i].Start);
                Assert.Equal(expected[i]["end"]!.GetValue<int>(), actual[i].End);
            }
        }
    }

    [Fact]
    public void Months_formatting_and_union_follow_python()
    {
        foreach (var (months, text) in Cases["format_months"]!.AsObject()) Assert.Equal(text!.GetValue<string>(), ResumeProfile.FormatMonths(int.Parse(months)));
        Assert.Equal(Cases["months_for_roles"]!.GetValue<int>(), ResumeProfile.MonthsForRoles(Obj("profile_pt"), ["R1", "R2"], Today));
        Assert.Equal(5, ResumeProfile.UnionMonths([(0, 3), (2, 5), (10, 10)])); // sobreposição não conta em dobro
    }

    [Fact]
    public void Sequence_matcher_total_matches_difflib_on_known_cases()
    {
        // difflib: SequenceMatcher(None, a, b).ratio() = 2*M/T; os valores abaixo foram conferidos no Python.
        Assert.Equal(4, ResumeProfile.MatchingTotal("a b c d".Split(' '), "a b x c d".Split(' ')));
        Assert.Equal(1, ResumeProfile.MatchingTotal("x y z".Split(' '), "z y x".Split(' ')));
        Assert.Equal(0, ResumeProfile.MatchingTotal("a".Split(' '), "b".Split(' ')));
    }
}
