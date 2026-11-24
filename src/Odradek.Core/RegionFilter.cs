using System.Text.RegularExpressions;

namespace Odradek.Core;

/// <summary>Vagas de regiões excluídas (Índia): coleta, triagem e limpeza do histórico usam a mesma regra (<c>jobs/region_filter.py</c>).</summary>
public static partial class RegionFilter
{
    [GeneratedRegex(
        @"\b(?:india|bengaluru|bangalore|mumbai|navi mumbai|new delhi|delhi|hyderabad|chennai|pune|kolkata|noida|gurgaon|gurugram|" +
        @"ahmedabad|jaipur|kochi|coimbatore|indore|chandigarh|lucknow|thiruvananthapuram|karnataka|maharashtra|telangana|" +
        @"tamil nadu|uttar pradesh|haryana|gujarat|kerala)\b")]
    private static partial Regex LocationRegex();

    [GeneratedRegex(@"\b(?:anywhere|across|within|all over|pan)[ -](?:in )?india\b|\bindian (?:citizens?|nationals?|residents?)\b|₹")]
    private static partial Regex TextRegex();

    /// <summary>Motivo (não vazio) quando a vaga é da Índia: pelo local, ou por pistas explícitas no anúncio.</summary>
    public static string ExcludedReason(string? location, string? title, string? description)
    {
        location ??= "";
        // Lista longa de países ("USA, UK, India, Australia, …") é vaga multirregião, não vaga da Índia.
        if (location.Count(c => c == ',') < 4 && LocationRegex().IsMatch(Text.NormalizeText(location)))
            return $"local da vaga na Índia ({Text.Take(location, 60)})";
        var text = $"{title ?? ""} {Text.CleanJobHtml(description)}";
        return TextRegex().IsMatch(text.Replace("ß", "ss", StringComparison.Ordinal).ToLowerInvariant()) ? "anúncio restrito à Índia" : "";
    }
}
