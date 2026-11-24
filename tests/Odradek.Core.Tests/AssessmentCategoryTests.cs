using System.Text.Json;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>A categoria do requisito alimenta as pílulas de skills da tela, mas não pode vazar para o JSON da API local (paridade com o Python).</summary>
public class AssessmentCategoryTests
{
    private const string Raw = """
        {"requirements":[
          {"id":"Q1","text":"C# e .NET","kind":"must","category":"skill","status":"met","realism":"core"},
          {"id":"Q2","text":"3 anos de experiência","kind":"must","category":"experience_years","status":"partial","realism":"core"}
        ],"responsibilities":[],"decision":{}}
        """;

    [Fact]
    public void Compact_reads_the_requirement_category()
    {
        var assessment = AssessmentReader.Compact(Raw)!;
        Assert.Equal(["skill", "experience_years"], assessment.Requirements.Select(r => r.Category));
    }

    [Fact]
    public void Category_is_not_serialized_to_the_api_json()
    {
        var json = JsonSerializer.Serialize(AssessmentReader.Compact(Raw)!, Json.Options);
        Assert.DoesNotContain("category", json);
    }
}
