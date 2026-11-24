namespace Odradek.Core;

/// <summary>
/// Telefone para o preenchimento de formulários: separa o código do país (DDI) do número nacional, porque muitos sites têm um campo para cada.
/// A regra espelha a da extensão (<c>phoneParts</c> em <c>job-autofill/content.js</c>), com o DDI configurado no perfil como dica.
/// </summary>
public static class PhoneNumber
{
    /// <summary>Os mesmos códigos que a extensão reconhece em números internacionais.</summary>
    private static readonly HashSet<string> CallingCodes =
    [
        "1", "7", "20", "27", "30", "31", "32", "33", "34", "36", "39", "40", "41", "43", "44", "45", "46", "47", "48", "49", "51", "52", "53", "54",
        "55", "56", "57", "58", "60", "61", "62", "63", "64", "65", "66", "81", "82", "84", "86", "90", "91", "92", "93", "94", "95", "98", "212",
        "213", "216", "218", "351", "352", "353", "354", "355", "356", "357", "358", "359", "370", "371", "372", "380", "381", "385", "386", "420",
        "421", "591", "593", "595", "598", "971", "972",
    ];

    /// <summary>"+55", "0055", " 55 " → "55". Vazio ou com algo que não seja número → <c>""</c>.</summary>
    public static string NormalizeCountryCode(string? raw)
    {
        var text = (raw ?? "").Trim().Replace(" ", "").Replace("-", "");
        if (text.StartsWith('+')) text = text[1..];
        else if (text.StartsWith("00")) text = text[2..];
        return text.Length > 0 && text.All(char.IsAsciiDigit) ? text : "";
    }

    /// <summary>1 a 3 dígitos, sem zero na frente (formato do plano de numeração internacional).</summary>
    public static bool IsValidCountryCode(string digits) => digits.Length is >= 1 and <= 3 && digits.All(char.IsAsciiDigit) && digits[0] != '0';

    /// <summary>
    /// Separa o telefone em (DDI, número nacional só com dígitos). Com <c>+</c> ou <c>00</c> na frente o número já traz o próprio DDI e manda
    /// (o configurado só desempata); sem isso, o DDI configurado vale e o número é tratado como nacional (exceto 55 + 10/11 dígitos, que
    /// já vem completo). Sem DDI configurado e sem <c>+</c> devolve só o número: quem preenche decide (a extensão assume Brasil).
    /// </summary>
    public static (string CountryCode, string National) Split(string? raw, string? configuredCountryCode)
    {
        var text = (raw ?? "").Trim();
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return ("", "");
        var configured = NormalizeCountryCode(configuredCountryCode);
        if (!IsValidCountryCode(configured)) configured = "";

        var international = text.StartsWith('+') || text.StartsWith("00");
        if (text.StartsWith("00")) digits = digits[2..];
        if (international)
        {
            if (configured.Length > 0 && digits.Length > configured.Length && digits.StartsWith(configured, StringComparison.Ordinal))
                return (configured, digits[configured.Length..]);
            foreach (var length in new[] { 3, 2, 1 })
                if (digits.Length > length && CallingCodes.Contains(digits[..length])) return (digits[..length], digits[length..]);
            return ("", digits);
        }
        if (configured.Length == 0) return ("", digits);
        if (configured == "55" && digits.Length is 12 or 13 && digits.StartsWith("55", StringComparison.Ordinal)) return ("55", digits[2..]);
        return (configured, digits);
    }
}
