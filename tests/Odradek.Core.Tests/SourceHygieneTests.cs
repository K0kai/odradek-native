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
    public void Default_ai_instructions_keep_the_contract_with_the_code()
    {
        var main = Collector.FilterPrompts.DefaultMain;
        var secondary = Collector.FilterPrompts.DefaultSecondary;
        var selection = Engines.TriageService.DefaultJobSelectionPrompt;
        foreach (var prompt in new[] { main, secondary, selection }) Assert.DoesNotContain("\r", prompt);

        // marcadores que o RenderPrompt preenche; sem eles o modelo não recebe o perfil nem as preferências
        foreach (var key in new[] { "{profile}", "{pref_keywords}", "{facts_pt}", "{facts_en}" })
        {
            Assert.Contains(key, main);
            Assert.Contains(key, secondary);
        }
        Assert.Contains("{area_hint}", secondary);
        Assert.Contains("{main_filter}", secondary);
        Assert.DoesNotContain("{", selection); // o texto de seleção entra como está, sem marcadores

        // regras que o código confere depois (Verify): o prompt precisa pedir exatamente isso
        foreach (var prompt in new[] { main, secondary })
        {
            Assert.Contains("evidence_ids", prompt);
            Assert.Contains("anchor_role_ids", prompt);
            Assert.Contains("experience_levels", prompt);
            Assert.Contains("[R1]", prompt);
            Assert.Contains("SAME role", prompt);
        }
        Assert.Contains("[T1]", secondary);
        Assert.Contains("sector", secondary);
        Assert.Contains("resilience_reason", secondary);

        // nenhum marcador sobra sem preencher depois de renderizar
        var filled = Engines.FilterGenerator.RenderPrompt(secondary, "kw", "fatos", "facts", profile: "perfil", areaHint: "saúde", mainFilter: "Software Engineer");
        Assert.DoesNotContain("{profile}", filled);
        Assert.DoesNotContain("{main_filter}", filled);
        Assert.DoesNotContain("{area_hint}", filled);
        Assert.Contains("Software Engineer", filled);

        // instruções enxutas: o limite protege o orçamento de tokens de cada chamada
        Assert.True(main.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 800, "filtro principal longo demais");
        Assert.True(secondary.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 850, "filtro secundário longo demais");
        Assert.True(selection.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 350, "instruções de seleção longas demais");
    }
}
