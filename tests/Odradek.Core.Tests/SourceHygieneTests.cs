namespace Odradek.Core.Tests;

/// <summary>
/// Os prompts de IA são literais de texto bruto nos fontes: o compilador usa a quebra de linha do ARQUIVO. Com CRLF o prompt mudaria
/// em relação ao do app Python (e os testes de paridade quebrariam), então os fontes com prompts precisam ficar em LF.
/// </summary>
public class SourceHygieneTests
{
    private static string CoreSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Odradek.Core"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Odradek.Core", relative);
    }

    [Theory]
    [InlineData("Candidate/JobAssessment.cs")]
    [InlineData("Engines/TriageService.cs")]
    [InlineData("Candidate/ResumeAnalysis.cs")]
    [InlineData("Engines/Assistant.cs")]
    [InlineData("Collector/FilterPrompts.cs")]
    public void Files_that_hold_ai_prompts_use_lf_line_endings(string relative)
    {
        var bytes = File.ReadAllBytes(CoreSource(relative));
        Assert.False(bytes.Contains((byte)'\r'), $"{relative} tem CRLF: converta para LF (os prompts herdam a quebra de linha do arquivo).");
    }

    [Fact]
    public void Prompt_constants_have_no_carriage_returns()
    {
        Assert.DoesNotContain('\r', Engines.TriageService.DefaultJobSelectionPrompt);
        Assert.DoesNotContain('\r', Engines.TriageService.ExperiencePriorityRule);
    }

    [Fact]
    public void Default_filter_prompts_are_identical_to_the_python_ones()
    {
        var expected = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "filter_prompts.json")))!.AsObject();
        Assert.Equal(Collector.FilterPrompts.DefaultMain, expected["main"]!.GetValue<string>());
        Assert.Equal(Collector.FilterPrompts.DefaultSecondary, expected["secondary"]!.GetValue<string>());
    }
}
