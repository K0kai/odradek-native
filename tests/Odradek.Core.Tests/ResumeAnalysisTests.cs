using System.Text.Json.Nodes;
using Odradek.Core.Candidate;

namespace Odradek.Core.Tests;

/// <summary>Paridade com <c>candidate/resume_pipeline.py</c> e <c>resume_profile.legacy_analysis</c> (respostas de IA e currículos sintéticos).</summary>
public class ResumeAnalysisTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "resume_analysis_cases.json")))!;
    private static readonly JsonObject Profiles = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "resume_profile_cases.json")))!;
    private static readonly DateOnly Today = new(2026, 11, 23);

    [Fact]
    public void Profile_schema_matches_python() => Assert.Null(JsonCompare.Diff(Cases["schema"], ResumeAnalysis.ProfileSchema()));

    [Fact]
    public void Profile_prompt_matches_python_including_the_40000_character_cut()
    {
        foreach (var item in Cases["prompts"]!.AsArray().OfType<JsonObject>())
        {
            var actual = ResumeAnalysis.BuildProfilePrompt(item["text"]!.GetValue<string>(), item["language_name"]!.GetValue<string>());
            Assert.True(item["prompt"]!.GetValue<string>() == actual, $"prompt difere (Python {item["prompt"]!.GetValue<string>().Length} chars, C# {actual.Length})");
        }
    }

    [Fact]
    public void Legacy_analysis_and_dossier_match_python_for_every_profile_shape()
    {
        foreach (var item in Cases["legacy"]!.AsArray().OfType<JsonObject>())
        {
            var key = item["profile_key"]!.GetValue<string>();
            JsonNode profile = key switch { "pt" => Profiles["profile_pt"]!, "en" => Profiles["profile_en"]!, "merged" => Profiles["merged"]!, _ => new JsonObject { ["version"] = "2" } };
            var language = item["language"]!.GetValue<string>();
            var analysis = ResumeAnalysis.LegacyAnalysis((JsonObject)profile.DeepClone(), language);
            Assert.True(JsonCompare.Diff(item["analysis"], analysis) is null, $"{key}/{language}: {JsonCompare.Diff(item["analysis"], analysis)}");
            Assert.Equal(item["analysis"]!.AsObject().Select(kv => kv.Key), analysis.Select(kv => kv.Key)); // a ordem das chaves vai para o banco
            Assert.Equal(item["dossier"]!.GetValue<string>(), ResumeAnalysis.ComposeDossier(analysis, language));
        }
    }

    [Fact]
    public void Dossier_handles_odd_shapes_like_python()
    {
        var odd = (JsonObject)Cases["odd_dossier"]!["analysis"]!.DeepClone();
        Assert.Equal(Cases["odd_dossier"]!["pt"]!.GetValue<string>(), ResumeAnalysis.ComposeDossier(odd, "pt"));
        Assert.Equal(Cases["odd_dossier"]!["en"]!.GetValue<string>(), ResumeAnalysis.ComposeDossier(odd, "en"));
        Assert.Equal(Cases["empty_dossier"]!["pt"]!.GetValue<string>(), ResumeAnalysis.ComposeDossier([], "pt"));
        Assert.Equal(Cases["empty_dossier"]!["en"]!.GetValue<string>(), ResumeAnalysis.ComposeDossier(new JsonObject { ["headline"] = "só título" }, "en"));
    }

    [Fact]
    public void Current_role_detection_matches_python()
    {
        foreach (var item in Cases["is_current"]!.AsArray().OfType<JsonObject>())
            Assert.Equal(item["result"]!.GetValue<bool>(), ResumeAnalysis.ExperienceIsCurrent(item["value"]!.DeepClone()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Analysis_with_a_synthetic_ai_reply_matches_python_end_to_end(int index)
    {
        var item = Cases["analysis_cases"]![index]!.AsObject();
        string? seenPrompt = null;
        var analysis = await ResumeAnalysis.AnalyzeAsync(item["text"]!.GetValue<string>(), item["language"]!.GetValue<string>(), "gemini", "m", "k", null,
            (provider, model, key, prompt, _) =>
            {
                Assert.Equal(("gemini", "m", "k"), (provider, model, key));
                seenPrompt = prompt;
                return Task.FromResult((JsonObject)item["raw"]!.DeepClone());
            }, Today);
        Assert.Equal(item["prompt"]!.GetValue<string>(), seenPrompt);
        Assert.Null(JsonCompare.Diff(item["expected"], analysis));
        Assert.Equal("_profile", analysis.Last().Key);
    }

    [Fact]
    public async Task Analysis_needs_a_key_and_rejects_replies_without_verifiable_facts()
    {
        var text = Cases["analysis_cases"]![0]!["text"]!.GetValue<string>();
        var ex = await Assert.ThrowsAsync<Ai.AiUnavailableException>(() => ResumeAnalysis.AnalyzeAsync(text, "pt", "gemini", "m", "", null));
        Assert.Equal("Configure a chave da API de IA antes de analisar o currículo.", ex.Message);
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() => ResumeAnalysis.AnalyzeAsync(text, "pt", "gemini", "m", "k", null,
            (_, _, _, _, _) => Task.FromResult(JsonNode.Parse("""{"roles":[{"title":"Inventado","quote":"não está no texto"}],"skills":[]}""")!.AsObject()), Today));
        Assert.Equal("A análise não retornou fatos verificáveis no texto do PDF.", empty.Message);
    }
}
