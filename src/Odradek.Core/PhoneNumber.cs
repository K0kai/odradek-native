namespace Odradek.Core;

/// <summary>
/// Código do país (DDI) do telefone do perfil, guardado à parte porque muitos sites têm um campo só para ele.
/// </summary>
public static class PhoneNumber
{
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
}
