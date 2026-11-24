using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Odradek.Core.Data;

namespace Odradek.Core.Candidate;

/// <summary>Linha de <c>resumes</c> que a triagem, a carta e o envio por e-mail precisam.</summary>
public sealed class ResumeRecord
{
    public long Id { get; set; }
    public string Language { get; set; } = "";
    public string AnalysisSummary { get; set; } = "";
    public string ExtractedText { get; set; } = "";
    public string ProfileJson { get; set; } = "";
}

/// <summary>Leituras de currículo (<c>resume_summaries</c>, <c>get_resume</c>, <c>merged_profile</c>).</summary>
public static class ResumeData
{
    public static Dictionary<string, string> Summaries(SqliteConnection connection) =>
        connection.Query<(string Language, string? Summary)>("SELECT language, analysis_summary AS Summary FROM resumes")
            .ToDictionary(r => r.Language, r => r.Summary ?? "");

    public static ResumeRecord? Get(SqliteConnection connection, string language) =>
        connection.QueryFirstOrDefault<ResumeRecord>(
            @"SELECT id, language, COALESCE(analysis_summary,'') AS analysis_summary, COALESCE(extracted_text,'') AS extracted_text,
                     COALESCE(profile_json,'') AS profile_json FROM resumes WHERE language=@language", new { language });

    /// <summary>Perfil fundido PT+EN (+ portfólio por último, para o PDF ter precedência) e o texto normalizado dos dois para checar citações.</summary>
    public static (JsonObject Profile, NormalizedText Norm)? MergedProfile(Database db, string prefer = "pt")
    {
        List<(string Language, string? ProfileJson, string? Text)> rows;
        using (var connection = db.Open())
            rows = connection.Query<(string Language, string? ProfileJson, string? Text)>(
                "SELECT language, profile_json AS ProfileJson, extracted_text AS Text FROM resumes").ToList();
        var ordered = rows.OrderBy(r => r.Language == prefer ? 0 : 1).ToList(); // OrderBy é estável, como o sorted do Python
        var profiles = new List<JsonObject>();
        var texts = new List<string>();
        foreach (var row in ordered)
        {
            if (ResumeProfile.ParseProfile(row.ProfileJson) is not { } profile) continue;
            profiles.Add(profile);
            texts.Add(row.Text ?? "");
        }
        if (PortfolioSource.Load(db, prefer) is { } portfolio)
        {
            profiles.Add(portfolio.Profile);
            texts.Add(portfolio.Text);
        }
        var merged = ResumeProfile.MergeProfiles(profiles);
        if (merged is null) return null;
        if (!merged.ContainsKey("derived")) // só portfólio: nada passou por GroundProfile
        {
            merged["derived"] = ResumeProfile.ComputeDerived(merged);
            merged["coverage"] = new JsonObject { ["kept"] = 0, ["dropped"] = new JsonObject(), ["ungrounded"] = 0 };
        }
        return (merged, NormalizedText.Of(string.Join('\n', texts)));
    }
}
