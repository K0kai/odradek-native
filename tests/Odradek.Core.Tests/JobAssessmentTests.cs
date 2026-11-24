using System.Text.Json;
using System.Text.Json.Nodes;
using Odradek.Core.Candidate;

namespace Odradek.Core.Tests;

/// <summary>Comparação de JSON que ignora a ordem das chaves e trata 1 e 1.0 como o mesmo número.</summary>
public static class JsonCompare
{
    private static double Num(JsonValue value) => double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public static string? Diff(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        switch (expected, actual)
        {
            case (null, null): return null;
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(kv => kv.Key).Union(a.Select(kv => kv.Key)))
                {
                    if (!e.ContainsKey(key)) return $"{path}.{key}: só no C# = {a[key]?.ToJsonString()}";
                    if (!a.ContainsKey(key)) return $"{path}.{key}: só no Python = {e[key]?.ToJsonString()}";
                    if (Diff(e[key], a[key], $"{path}.{key}") is { } inner) return inner;
                }
                return null;
            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count) return $"{path}: {e.Count} itens no Python, {a.Count} no C# ({e.ToJsonString()} × {a.ToJsonString()})";
                for (var i = 0; i < e.Count; i++)
                    if (Diff(e[i], a[i], $"{path}[{i}]") is { } inner) return inner;
                return null;
            case (JsonValue e, JsonValue a):
                if (e.GetValueKind() == JsonValueKind.Number && a.GetValueKind() == JsonValueKind.Number)
                    return Num(e) == Num(a) ? null : $"{path}: Python {e.ToJsonString()}, C# {a.ToJsonString()}";
                return e.ToJsonString() == a.ToJsonString() ? null : $"{path}: Python {e.ToJsonString()}, C# {a.ToJsonString()}";
            default:
                return $"{path}: tipos diferentes ({expected?.ToJsonString()} × {actual?.ToJsonString()})";
        }
    }
}

/// <summary>Paridade com <c>jobs/job_assessment.py</c>: 16 cenários gerados pelo Python com vaga e respostas de IA sintéticas.</summary>
public class JobAssessmentTests
{
    private static readonly JsonObject Cases = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "assessment_cases.json")))!;

    public static IEnumerable<object[]> Names() => Cases["cases"]!.AsArray().Select(c => new object[] { c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Assessment_matches_python_exactly(string name)
    {
        var item = Cases["cases"]!.AsArray().OfType<JsonObject>().Single(c => c["name"]!.GetValue<string>() == name);
        var job = item["job"]!.AsObject();
        var cfg = item["cfg"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
        var profile = (JsonObject)Cases["profile"]!.DeepClone();
        var resumeNorm = NormalizedText.Of(Cases["resume_norm_text"]!.GetValue<string>());
        var prompts = new List<string>();
        var raws = new[] { item["raw_assess"]!.AsObject(), item["raw_skeptic"]!.AsObject() };
        var progress = new List<string>();

        CallJson call = (prompt, schema, max, thinking, _) =>
        {
            var expectedCall = item["calls"]!.AsArray()[prompts.Count]!.AsObject();
            Assert.Equal(expectedCall["schema"]!.GetValue<string>(), prompts.Count == 0 ? "assess" : "skeptic");
            Assert.Equal(expectedCall["max"]!.GetValue<int>(), max);
            Assert.Equal(expectedCall["thinking"]!.GetValue<int>(), thinking);
            prompts.Add(prompt);
            return Task.FromResult((JsonObject)raws[Math.Min(prompts.Count - 1, 1)].DeepClone());
        };

        var outcome = await JobAssessment.AssessAsync(
            new AssessJob(job["title"]!.GetValue<string>(), job["company"]!.GetValue<string>(), job["location"]!.GetValue<string>(), job["description"]!.GetValue<string>(),
                job["language"]!.GetValue<string>(), job["search_remote"]!.GetValue<string>()),
            cfg, profile, resumeNorm, call, item["language"]!.GetValue<string>(), "Prefer remote roles.", item["track"]!.GetValue<string>(), progress.Add);

        var expected = item["expected"]!.AsObject();
        var expectedPrompts = item["prompts"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        Assert.Equal(expectedPrompts.Count, prompts.Count);
        for (var i = 0; i < prompts.Count; i++) Assert.True(expectedPrompts[i] == prompts[i], FirstDifference(i, expectedPrompts[i], prompts[i]));
        Assert.Equal(item["progress"]!.AsArray().Select(p => p!.GetValue<string>()), progress);
        Assert.Equal(expected["match_score"]!.GetValue<int>(), outcome.MatchScore);
        Assert.Equal(expected["should_apply"]!.GetValue<bool>(), outcome.ShouldApply);
        Assert.Equal(expected["letter_required"]!.GetValue<bool>(), outcome.LetterRequired);
        Assert.Equal(expected["reason"]!.GetValue<string>(), outcome.Reason);
        Assert.Equal(expected["track"]!.GetValue<string>(), outcome.Track);
        Assert.Equal(expected["bridges"]!.AsArray().Select(b => b!.GetValue<string>()), outcome.Bridges);
        Assert.Null(JsonCompare.Diff(expected["assessment"], outcome.Assessment));
    }

    private static string FirstDifference(int index, string python, string csharp)
    {
        var at = 0;
        while (at < python.Length && at < csharp.Length && python[at] == csharp[at]) at++;
        string Window(string text) => text[Math.Max(0, at - 60)..Math.Min(text.Length, at + 80)].Replace("\n", "|");
        return $"prompt {index} difere na posição {at} (Python {python.Length} chars, C# {csharp.Length}): Python=[{Window(python)}] C#=[{Window(csharp)}]";
    }

    [Fact]
    public void Optimism_curve_matches_python()
    {
        foreach (var (input, value) in Cases["optimism"]!.AsObject())
            Assert.Equal(value!.GetValue<double>(), JobAssessment.OptimismThreshold(input), 12);
    }

    [Fact]
    public void Json_schemas_match_the_python_definitions()
    {
        Assert.Null(JsonCompare.Diff(Cases["assess_schema"], JobAssessment.AssessSchema()));
        Assert.Null(JsonCompare.Diff(Cases["skeptic_schema"], JobAssessment.SkepticSchema()));
    }

    [Fact]
    public void Clean_job_html_matches_python()
    {
        foreach (var item in Cases["clean_html"]!.AsArray().OfType<JsonObject>())
            Assert.Equal(item["text"]!.GetValue<string>(), Core.Text.CleanJobHtml(item["raw"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_scenarios_cover_approvals_rejections_blocks_and_the_skeptic_step()
    {
        var results = new List<(bool Apply, int Blocks, int Calls)>();
        foreach (var item in Cases["cases"]!.AsArray().OfType<JsonObject>())
            results.Add((item["expected"]!["should_apply"]!.GetValue<bool>(), item["expected"]!["assessment"]!["decision"]!["blocks"]!.AsArray().Count, item["calls"]!.AsArray().Count));
        Assert.Contains(results, r => r.Apply);
        Assert.Contains(results, r => !r.Apply && r.Blocks > 0);
        Assert.Contains(results, r => !r.Apply && r.Blocks == 0);
        Assert.Contains(results, r => r.Calls == 2);
        Assert.Contains(results, r => r.Calls == 1);
        await Task.CompletedTask;
    }
}
