using System.Text.RegularExpressions;

namespace Odradek.Core.Collector;

/// <summary>
/// Idioma da vaga (pt/en). O Python usa o Lingua quando instalado; aqui vale o mesmo plano B por palavras comuns que o <c>app.py:69</c> já tem
/// (no banco atual ele concorda com a coluna <c>jobs.language</c> em ~98,5% das vagas).
/// </summary>
public static partial class LanguageDetector
{
    private static readonly string[] Portuguese = ["de", "para", "com", "uma", "não", "você", "experiência", "responsabilidades", "requisitos", "benefícios", "trabalho"];
    private static readonly string[] English = ["the", "and", "with", "for", "you", "experience", "responsibilities", "requirements", "benefits", "work", "team"];

    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"[^a-záàâãéêíóôõúüç ]")] private static partial Regex Other();

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    public static (string Language, double Confidence) Detect(string? text)
    {
        var sample = Text.Take(Spaces().Replace(Tags().Replace(text ?? "", " "), " ").Trim(), 12000);
        if (sample.Length < 30) return ("unknown", 0.0);
        var normalized = " " + Other().Replace(sample.ToLowerInvariant(), " ") + " ";
        var pt = Portuguese.Sum(word => Count(normalized, $" {word} "));
        var en = English.Sum(word => Count(normalized, $" {word} "));
        var total = pt + en;
        if (total < 2 || pt == en) return ("unknown", 0.0);
        var best = Math.Max(pt, en);
        return (pt > en ? "pt" : "en", Math.Min(0.9, 0.55 + best / (double)(total + 2) * 0.4));
    }
}
