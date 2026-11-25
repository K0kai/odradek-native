using System.Text.Json.Nodes;
using Odradek.Core.Candidate;

namespace Odradek.Core.Tests;

/// <summary>
/// A revisão cética não derruba sozinha o que o programa já verificou: contestar requisito com evidência relacionada vale no máximo "leve", só restrição
/// explícita (diploma, licença, idioma, local, autorização) bloqueia, tempo de experiência é medido pelas datas, e "problema novo" que repete um item é contestação dele.
/// </summary>
public class SkepticReviewTests
{
    private const string JobText = "Full stack developer. Requires solid experience with React and Next.js. Requires a degree in Information Systems. "
        + "Requires 5+ years of Python. Knowledge of Kubernetes. Develop screens and features for the web product. Mode: remote.";

    private const string ResumeText = "Web developer Acme Built React screens for the Acme web product React Python Community platform with Next.js, React and TypeScript";

    private static JsonObject Profile()
    {
        var profile = new JsonObject
        {
            ["version"] = "2",
            ["roles"] = new JsonArray(new JsonObject
            {
                ["id"] = "R1", ["title"] = "Web developer", ["employer"] = "Acme", ["start"] = "2022-01", ["end"] = "2023-12", ["quote"] = "Web developer Acme",
                ["responsibilities"] = new JsonArray(new JsonObject { ["id"] = "R1.1", ["text"] = "Built React screens for the Acme web product", ["quote"] = "Built React screens for the Acme web product" }),
                ["skills_used"] = new JsonArray("react"),
            }),
            ["skills"] = new JsonArray(
                new JsonObject { ["id"] = "S1", ["name"] = "React", ["canonical"] = "react", ["category"] = "framework", ["quotes"] = new JsonArray("React") },
                new JsonObject { ["id"] = "S2", ["name"] = "Python", ["canonical"] = "python", ["category"] = "language", ["quotes"] = new JsonArray("Python") }),
            ["transferable_skills"] = new JsonArray(), ["certifications"] = new JsonArray(), ["achievements"] = new JsonArray(), ["languages"] = new JsonArray(), ["education"] = new JsonArray(),
            ["projects"] = new JsonArray(new JsonObject
            {
                ["id"] = "P1", ["text"] = "Community platform with Next.js, React and TypeScript", ["quote"] = "Community platform with Next.js, React and TypeScript",
            }),
        };
        profile["derived"] = ResumeProfile.ComputeDerived(profile);
        return profile;
    }

    private static JsonObject Requirement(string id, string category, string quote, string text, double years = 0)
    {
        var item = new JsonObject { ["id"] = id, ["text"] = text, ["kind"] = "must", ["category"] = category, ["job_quote"] = quote, ["realism"] = "core" };
        if (years > 0) item["years_required"] = years;
        return item;
    }

    private static JsonObject Match(string id, string status, string quote, params string[] evidence) => new()
    {
        ["requirement_id"] = id, ["status"] = status, ["evidence_ids"] = new JsonArray(evidence.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()), ["resume_quote"] = quote, ["bridge"] = "",
    };

    private static JsonObject RawAssess(string react = "Next.js, React and TypeScript") => new()
    {
        ["requirements"] = new JsonArray(
            Requirement("Q1", "skill", "Requires solid experience with React and Next.js", "Solid experience with React and Next.js"),
            Requirement("Q2", "degree", "Requires a degree in Information Systems", "Degree in Information Systems"),
            Requirement("Q3", "experience_years", "Requires 5+ years of Python", "5+ years of Python", years: 5),
            Requirement("Q4", "skill", "Knowledge of Kubernetes", "Knowledge of Kubernetes")),
        ["role_core"] = new JsonObject
        {
            ["responsibilities"] = new JsonArray(new JsonObject { ["id"] = "J1", ["text"] = "Develop screens and features for the web product", ["job_quote"] = "Develop screens and features for the web product" }),
        },
        ["matches"] = new JsonArray(
            Match("Q1", "met", react, "P1", "S1"), Match("Q2", "unmet", ""), Match("Q3", "met", "Python", "S2"), Match("Q4", "unmet", ""),
            Match("J1", "met", "Built React screens for the Acme web product", "R1.1")),
    };

    private static List<JsonObject> Review(string kind, string requirementId, string severity, string quote)
    {
        var profile = Profile();
        var jobNorm = NormalizedText.Of(JobText);
        var verified = JobAssessment.Verify(RawAssess(), jobNorm, NormalizedText.Of(ResumeText), profile);
        var raw = new JsonObject
        {
            ["objections"] = new JsonArray(new JsonObject { ["kind"] = kind, ["requirement_id"] = requirementId, ["severity"] = severity, ["job_quote"] = quote, ["reason"] = "r" }),
        };
        return JobAssessment.VerifySkeptic(raw, jobNorm, verified, profile);
    }

    private static string S(JsonObject item, string key) => item[key]!.GetValue<string>();

    [Fact]
    public void Contesting_a_requirement_with_verified_related_evidence_is_at_most_minor()
    {
        var result = Assert.Single(Review("requirement_unmet", "Q1", "blocker", "Requires solid experience with React and Next.js"));
        Assert.Equal("minor", S(result, "severity"));
        Assert.Equal("requirement_unmet", S(result, "kind"));
    }

    [Fact]
    public void A_new_issue_that_repeats_a_listed_duty_becomes_a_contest_of_that_duty_and_gets_the_same_limits()
    {
        var result = Assert.Single(Review("new_issue", "", "major", "Develop screens and features for the web product"));
        Assert.Equal("requirement_unmet", S(result, "kind"));
        Assert.Equal("J1", S(result, "requirement_id"));
        Assert.Equal("minor", S(result, "severity"));
    }

    [Fact]
    public void A_skill_requirement_without_evidence_cannot_be_a_blocker_but_stays_major()
    {
        Assert.Equal("major", S(Assert.Single(Review("requirement_unmet", "Q4", "blocker", "Knowledge of Kubernetes")), "severity"));
    }

    [Fact]
    public void An_explicit_restriction_not_met_can_still_block()
    {
        Assert.Equal("blocker", S(Assert.Single(Review("requirement_unmet", "Q2", "blocker", "Requires a degree in Information Systems")), "severity"));
    }

    [Fact]
    public void Experience_time_is_measured_by_the_dates_not_by_the_skeptic()
    {
        Assert.Equal("minor", S(Assert.Single(Review("requirement_unmet", "Q3", "blocker", "Requires 5+ years of Python")), "severity"));
    }

    [Fact]
    public void A_new_issue_that_matches_no_listed_item_keeps_its_severity()
    {
        var result = Assert.Single(Review("new_issue", "", "blocker", "Full stack developer. Requires solid"));
        Assert.Equal("new_issue", S(result, "kind"));
        Assert.Equal("blocker", S(result, "severity"));
    }

    [Fact]
    public void An_unknown_id_is_fixed_by_the_quote_when_it_names_a_listed_item_and_dropped_otherwise()
    {
        var fixedById = Assert.Single(Review("requirement_unmet", "Q99", "blocker", "Knowledge of Kubernetes"));
        Assert.Equal("Q4", S(fixedById, "requirement_id"));
        Assert.Empty(Review("requirement_unmet", "Q99", "major", "Mode: remote"));
    }

    [Fact]
    public void An_objection_whose_quote_is_not_in_the_job_is_still_dropped()
    {
        Assert.Empty(Review("new_issue", "", "blocker", "texto que não está na vaga"));
    }

    [Fact]
    public void The_shown_quote_is_swapped_for_the_cited_evidence_that_is_about_the_requirement()
    {
        // O modelo citou um trecho real do currículo, mas de outro assunto (Laravel) para um requisito de React/Next.js.
        var resume = ResumeText + " Upgrade do backend white-label de entregas de Laravel 8 para 13";
        var raw = RawAssess("Upgrade do backend white-label de entregas de Laravel 8 para 13");
        var verified = JobAssessment.Verify(raw, NormalizedText.Of(JobText), NormalizedText.Of(resume), Profile());
        var q1 = verified.Requirements.Single(r => S(r, "id") == "Q1");
        Assert.Contains("React", S(q1, "resume_quote"));
        Assert.DoesNotContain("Laravel", S(q1, "resume_quote"));
        Assert.Equal("met", S(q1, "status"));
    }

    [Fact]
    public void A_quote_that_already_matches_the_requirement_is_kept()
    {
        var verified = JobAssessment.Verify(RawAssess("Next.js, React and TypeScript"), NormalizedText.Of(JobText), NormalizedText.Of(ResumeText), Profile());
        Assert.Equal("Next.js, React and TypeScript", S(verified.Requirements.Single(r => S(r, "id") == "Q1"), "resume_quote"));
    }

    [Fact]
    public void Both_triage_prompts_keep_the_projects_of_a_long_profile()
    {
        // O perfil real (PT + EN + portfólio) passa de 14 mil caracteres e os projetos vêm por último: com 9500 (e 6000 no cético) eram cortados
        // e a IA concluía "sem comprovação" de tecnologias que os projetos provam.
        var profile = Profile();
        var skills = (JsonArray)profile["skills"]!;
        for (var i = 3; i <= 900; i++)
            skills.Add(new JsonObject { ["id"] = $"S{i}", ["name"] = $"Skill{i}", ["canonical"] = $"skill{i}", ["category"] = "tool", ["quotes"] = new JsonArray($"Skill{i}") });
        profile["derived"] = ResumeProfile.ComputeDerived(profile);
        Assert.True(ResumeProfile.CompactProfileText(profile, 1_000_000).Length > 9500);
        Assert.True(ResumeProfile.CompactProfileText(profile, 1_000_000).Length < JobAssessment.ProfileBudget);

        var assess = JobAssessment.BuildAssessPrompt(new AssessJob("t", "c", "l", JobText), JobText, profile, new Dictionary<string, string>(), "en", "");
        var verified = JobAssessment.Verify(RawAssess(), NormalizedText.Of(JobText), NormalizedText.Of(ResumeText), profile);
        var skeptic = JobAssessment.BuildSkepticPrompt(JobText, profile, verified);
        Assert.Contains("[P1] Community platform with Next.js, React and TypeScript", assess);
        Assert.Contains("[P1] Community platform with Next.js, React and TypeScript", skeptic);
    }

    [Fact]
    public void The_skeptic_prompt_shows_the_cited_evidence_and_the_fair_review_rules()
    {
        var profile = Profile();
        var verified = JobAssessment.Verify(RawAssess(), NormalizedText.Of(JobText), NormalizedText.Of(ResumeText), profile);
        var prompt = JobAssessment.BuildSkepticPrompt(JobText, profile, verified);
        Assert.Contains("- Q1 [must/core] Solid experience with React and Next.js => met | evidence: P1,S1 | quote: \"Next.js, React and TypeScript\"", prompt);
        Assert.Contains("- J1 [duty] Develop screens and features for the web product => met | evidence: R1.1", prompt);
        Assert.Contains("Do NOT object about years", prompt);
        Assert.Contains("Portfolio projects ([P*]) are real evidence", prompt);
    }
}
