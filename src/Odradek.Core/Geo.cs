using System.Text.RegularExpressions;

namespace Odradek.Core;

/// <summary>Países com viés e idade das vagas (<c>app.py:1076-1117, 1825-1875</c> e <c>jobs/job_assessment.py:397</c>).</summary>
public static partial class Geo
{
    public const int PreferredCountriesMax = 5;

    private static readonly string[][] CountrySynonyms =
    [
        ["brazil", "brasil"], ["united states", "usa", "eua", "estados unidos"], ["united kingdom", "uk", "reino unido"],
        ["spain", "espanha", "espana"], ["germany", "alemanha"], ["canada"], ["portugal"],
    ];

    [GeneratedRegex(@"[,;\n]+")] private static partial Regex Separators();
    [GeneratedRegex(@"^(.*?)\s*\(([^()]*)\)\s*$", RegexOptions.Singleline)] private static partial Regex Place();
    [GeneratedRegex(@"(\d+)\s*(minute|minutos?|hour|horas?|day|dias?|week|semanas?|month|meses?)")] private static partial Regex RelativeLong();
    [GeneratedRegex(@"(\d+)\s*([mhdw])\b")] private static partial Regex RelativeShort();

    /// <summary>"Brazil (Belo Horizonte), Portugal" vira [("Brazil","Belo Horizonte"), ("Portugal","")]; sem repetir países.</summary>
    public static List<(string Name, string City)> ParsePreferred(string? raw)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>();
        foreach (var piece in Separators().Split(raw ?? ""))
        {
            var part = piece.Trim();
            if (part.Length == 0) continue;
            var m = Place().Match(part);
            var (name, city) = m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : (part, "");
            if (name.Length == 0 || !seen.Add(name.ToLowerInvariant())) continue;
            result.Add((Text.Take(name, 40), Text.Take(city, 60)));
        }
        return result;
    }

    /// <summary>Só os nomes, no máximo 5, na ordem dada.</summary>
    public static List<string> PreferredCountries(string? raw) => ParsePreferred(raw).Take(PreferredCountriesMax).Select(p => p.Name).ToList();

    internal static HashSet<string> CountryNames(string name)
    {
        var key = Text.NormalizeText(name);
        foreach (var group in CountrySynonyms)
            if (group.Contains(key)) return [.. group];
        return key.Length > 0 ? [key] : [];
    }

    private static readonly (string Code, string[] Names)[] CountryCodes =
    [
        ("BR", ["brazil", "brasil"]), ("US", ["united states", "usa", "eua", "estados unidos", "united states of america"]),
        ("GB", ["united kingdom", "uk", "reino unido", "great britain"]), ("PT", ["portugal"]), ("ES", ["spain", "espanha", "espana"]),
        ("DE", ["germany", "alemanha"]), ("CA", ["canada"]), ("AR", ["argentina"]), ("MX", ["mexico", "méxico"]), ("FR", ["france", "franca", "frança"]),
        ("NL", ["netherlands", "holanda", "paises baixos"]), ("IE", ["ireland", "irlanda"]), ("IT", ["italy", "italia", "itália"]),
        ("CL", ["chile"]), ("CO", ["colombia", "colômbia"]), ("AU", ["australia", "austrália"]), ("IN", ["india", "índia"]),
    ];

    /// <summary>Código ISO de 2 letras do país (nome em português ou inglês); <c>""</c> se não for um país conhecido ("remote", uma cidade).</summary>
    public static string CountryCode(string? name)
    {
        var key = Text.NormalizeText(name);
        if (key.Length == 0) return "";
        foreach (var (code, names) in CountryCodes)
            if (names.Any(n => Text.NormalizeText(n) == key)) return code;
        return "";
    }

    public static bool LocationInCountries(string? location, IReadOnlyList<string> countries)
    {
        var haystack = $" {Text.NormalizeText(location)} ";
        return countries.Any(country => CountryNames(country).Any(alias => haystack.Contains($" {alias} ", StringComparison.Ordinal)));
    }

    /// <summary>Idade em dias da postagem (ISO ou texto relativo do LinkedIn); <c>null</c> se não der para inferir.</summary>
    public static double? PostedAgeDays(string? postedAt)
    {
        if (string.IsNullOrEmpty(postedAt)) return null;
        var raw = postedAt.Trim();
        if (Clock.TryParse(raw, out var when)) return Math.Max(0, (DateTime.UtcNow - when).TotalSeconds / 86400.0);
        var low = raw.ToLowerInvariant();
        if (new[] { "just now", "agora", "moments ago", "seconds ago", "second ago", "today", "hoje" }.Any(low.Contains)) return 0.0;
        var m = RelativeLong().Match(low);
        if (!m.Success)
        {
            var short_ = RelativeShort().Match(low);
            if (!short_.Success) return null;
            var count = double.Parse(short_.Groups[1].Value);
            return short_.Groups[2].Value switch { "m" => count / 1440, "h" => count / 24, "d" => count, "w" => count * 7, _ => null };
        }
        var n = double.Parse(m.Groups[1].Value);
        var unit = m.Groups[2].Value;
        if (unit.StartsWith("min", StringComparison.Ordinal)) return n / 1440;
        if (unit.StartsWith("hour", StringComparison.Ordinal) || unit.StartsWith("hora", StringComparison.Ordinal)) return n / 24;
        if (unit.StartsWith("day", StringComparison.Ordinal) || unit.StartsWith("dia", StringComparison.Ordinal)) return n;
        if (unit.StartsWith("week", StringComparison.Ordinal) || unit.StartsWith("semana", StringComparison.Ordinal)) return n * 7;
        if (unit.StartsWith("month", StringComparison.Ordinal) || unit.StartsWith("mes", StringComparison.Ordinal)) return n * 30;
        return null;
    }

    /// <summary>Ordem de triagem: país com viés primeiro, depois o mais recente (data desconhecida por último).</summary>
    public static (int Group, double Age) RecencyKey(string? location, string? postedAt, IReadOnlyList<string> preferred)
    {
        var age = PostedAgeDays(postedAt);
        var inPreferred = preferred.Count > 0 && LocationInCountries(location, preferred);
        return (inPreferred || preferred.Count == 0 ? 0 : 1, age ?? 9999.0);
    }
}
