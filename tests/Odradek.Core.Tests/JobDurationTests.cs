using System.Text.Json.Nodes;
using Odradek.Core.Candidate;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>Exigências de tempo ("5+ anos de Python"): o código mede pelas datas do currículo e rebaixa o que a IA marcou como atendido sem prova.</summary>
public class JobDurationTests
{
    private const string JobText = "Backend engineer. Requires 5+ years of Python. Requires 3+ years of Python. Requires 8+ years of Python. " +
        "Requires 5 years of experience or a related degree. Requires 5 years of professional experience.";

    private static JsonObject Profile(bool datedSecondRole = true)
    {
        var profile = new JsonObject
        {
            ["version"] = "2",
            ["roles"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "R1", ["title"] = "Backend Developer", ["employer"] = "Acme", ["start"] = "2019-01", ["end"] = "2021-12", ["quote"] = "Backend Developer Acme",
                    ["responsibilities"] = new JsonArray(), ["skills_used"] = new JsonArray("python"),
                },
                new JsonObject
                {
                    ["id"] = "R2", ["title"] = "Support", ["employer"] = "Beta", ["start"] = datedSecondRole ? "2022-01" : "", ["end"] = datedSecondRole ? "2022-06" : "", ["quote"] = "Support Beta",
                    ["responsibilities"] = new JsonArray(), ["skills_used"] = new JsonArray(),
                }),
            ["skills"] = new JsonArray(new JsonObject { ["id"] = "S1", ["name"] = "Python", ["canonical"] = "python", ["category"] = "language", ["quotes"] = new JsonArray("Python") }),
            ["transferable_skills"] = new JsonArray(), ["certifications"] = new JsonArray(), ["projects"] = new JsonArray(), ["achievements"] = new JsonArray(), ["languages"] = new JsonArray(),
            ["education"] = new JsonArray(new JsonObject { ["id"] = "E1", ["text"] = "Bachelor in Computer Science", ["quote"] = "Bachelor in Computer Science", ["start"] = "2015-01", ["end"] = "2018-12" }),
        };
        profile["derived"] = ResumeProfile.ComputeDerived(profile);
        return profile;
    }

    private static JsonObject Requirement(string id, string quote, double years, double credit = 0)
    {
        var item = new JsonObject { ["id"] = id, ["text"] = quote, ["kind"] = "must", ["category"] = "experience_years", ["years_required"] = years, ["job_quote"] = quote, ["realism"] = "core" };
        if (credit > 0) item["education_credit_years"] = credit;
        return item;
    }

    private static JsonObject Match(string id, params string[] evidence) => new()
    {
        ["requirement_id"] = id, ["status"] = "met", ["evidence_ids"] = new JsonArray(evidence.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
        ["resume_quote"] = evidence.Contains("S1") ? "Python" : "Backend Developer Acme", ["bridge"] = "",
    };

    private static Dictionary<string, JsonObject> Run(JsonObject profile, JsonArray requirements, JsonArray matches)
    {
        var raw = new JsonObject { ["requirements"] = requirements, ["matches"] = matches };
        var verified = JobAssessment.Verify(raw, NormalizedText.Of(JobText), NormalizedText.Of("Python Backend Developer Acme Support Beta"), profile);
        return verified.Requirements.ToDictionary(r => r["id"]!.GetValue<string>());
    }

    [Fact]
    public void Skill_time_comes_from_the_dated_roles_that_used_it_and_the_status_follows_the_numbers()
    {
        var result = Run(Profile(),
            new JsonArray(Requirement("Q1", "5+ years of Python", 5), Requirement("Q2", "3+ years of Python", 3), Requirement("Q3", "8+ years of Python", 8)),
            new JsonArray(Match("Q1", "S1"), Match("Q2", "S1"), Match("Q3", "S1")));

        Assert.Equal("partial", result["Q1"]["status"]!.GetValue<string>()); // 3 anos de 5: pelo menos metade
        Assert.Equal("exige 5+ anos; comprovado 3 anos", result["Q1"]["note"]!.GetValue<string>());
        Assert.Equal(36, result["Q1"]["proven_months"]!.GetValue<int>());
        Assert.Equal("met", result["Q2"]["status"]!.GetValue<string>()); // 3 de 3
        Assert.Equal("unmet", result["Q3"]["status"]!.GetValue<string>()); // 3 de 8: menos da metade
    }

    [Fact]
    public void Without_a_skill_or_role_the_total_experience_is_used_and_education_credit_adds_only_what_the_posting_allows()
    {
        var result = Run(Profile(),
            new JsonArray(
                Requirement("Q1", "5 years of experience or a related degree", 5, credit: 2),
                Requirement("Q2", "5 years of professional experience", 5)),
            new JsonArray(Match("Q1", "R1"), Match("Q2", "R1")));
        // Q1: 36 meses de R1 + 24 de curso (limite de 2 anos que a vaga concede; a formação tem 48) = 5 anos
        Assert.Equal("met", result["Q1"]["status"]!.GetValue<string>());
        Assert.Equal(60, result["Q1"]["proven_months"]!.GetValue<int>());
        // Q2: só R1 (3 anos), sem crédito de curso
        Assert.Equal("partial", result["Q2"]["status"]!.GetValue<string>());

        var generic = Run(Profile(), new JsonArray(Requirement("Q1", "5 years of professional experience", 5)), new JsonArray(Match("Q1")));
        Assert.Equal(42, generic["Q1"]["proven_months"]!.GetValue<int>()); // sem citação: experiência total (36 + 6 meses)
    }

    [Fact]
    public void Roles_without_dates_never_count_against_the_candidate()
    {
        var profile = Profile(datedSecondRole: false);
        profile["roles"]![1]!["skills_used"] = new JsonArray("python"); // o cargo sem datas também usou Python
        profile["derived"] = ResumeProfile.ComputeDerived(profile);
        var undatedOnly = new JsonObject
        {
            ["version"] = "2", ["roles"] = new JsonArray(profile["roles"]![1]!.DeepClone()), ["skills"] = profile["skills"]!.DeepClone(), ["education"] = new JsonArray(),
            ["transferable_skills"] = new JsonArray(), ["certifications"] = new JsonArray(), ["projects"] = new JsonArray(), ["achievements"] = new JsonArray(), ["languages"] = new JsonArray(),
        };
        undatedOnly["derived"] = ResumeProfile.ComputeDerived(undatedOnly);

        var result = Run(undatedOnly, new JsonArray(Requirement("Q1", "5+ years of Python", 5)), new JsonArray(Match("Q1", "S1")));
        Assert.Equal("met", result["Q1"]["status"]!.GetValue<string>()); // sem datas, nada a rebaixar
        Assert.Contains("sem datas", result["Q1"]["note"]!.GetValue<string>());
    }

    [Fact]
    public void Requirements_that_ask_for_no_duration_keep_their_old_shape_and_the_reader_exposes_the_numbers()
    {
        var plain = Run(Profile(), new JsonArray(new JsonObject { ["id"] = "Q1", ["text"] = "Python", ["kind"] = "must", ["category"] = "skill", ["job_quote"] = "Python", ["realism"] = "core", ["years_required"] = 5 }),
            new JsonArray(Match("Q1", "S1")));
        Assert.False(plain["Q1"].ContainsKey("years_required")); // anos só valem para experience_years
        Assert.Equal("met", plain["Q1"]["status"]!.GetValue<string>());

        var json = new JsonObject
        {
            ["requirements"] = new JsonArray(new JsonObject { ["id"] = "Q1", ["text"] = "x", ["kind"] = "must", ["status"] = "partial", ["years_required"] = 5.0, ["proven_months"] = 36 }),
            ["responsibilities"] = new JsonArray(), ["decision"] = new JsonObject(),
        }.ToJsonString();
        var item = AssessmentReader.Compact(json)!.Requirements.Single();
        Assert.Equal(5.0, item.YearsRequired);
        Assert.Equal(36, item.ProvenMonths);
    }

    [Fact]
    public void Education_periods_are_kept_only_when_both_dates_are_known_and_show_in_the_profile_text()
    {
        const string text = "Bachelor in Computer Science, State University, 2015 - 2018. Certificate in Data, 2020.";
        var raw = new JsonObject
        {
            ["roles"] = new JsonArray(),
            ["skills"] = new JsonArray(),
            ["education"] = new JsonArray(
                new JsonObject { ["text"] = "Bachelor in Computer Science", ["quote"] = "Bachelor in Computer Science", ["start"] = "2015", ["end"] = "2018" },
                new JsonObject { ["text"] = "Certificate in Data", ["quote"] = "Certificate in Data", ["start"] = "", ["end"] = "" }),
        };
        var profile = ResumeProfile.GroundProfile(raw, text, new DateOnly(2026, 11, 24));
        var education = profile["education"]!.AsArray();
        Assert.Equal("2015-01", education[0]!["start"]!.GetValue<string>());
        Assert.Equal("2018-12", education[0]!["end"]!.GetValue<string>());
        Assert.False(education[1]!.AsObject().ContainsKey("start"));
        Assert.Equal(48, ResumeProfile.EducationMonths(profile, new DateOnly(2026, 11, 24)));
        Assert.Contains("(2015-01 → 2018-12)", ResumeProfile.CompactProfileText(profile));
    }
}
