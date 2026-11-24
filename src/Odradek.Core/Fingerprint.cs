using System.Security.Cryptography;
using System.Text;

namespace Odradek.Core;

/// <summary>
/// Identidade de uma vaga, igual à do Python (<c>app.py:747-755</c>): SHA-256 de
/// <c>url normalizada | empresa | título | local</c>. Se isto divergir, as vagas já coletadas voltam duplicadas.
/// </summary>
public static class Fingerprint
{
    public static string Of(string? url, string? company, string? title, string? location)
    {
        var text = string.Join('|', NormalizeUrl(url ?? ""), Fold(company), Fold(title), Fold(location));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Equivale a <c>f"{scheme.casefold()}://{netloc.casefold()}{path.rstrip('/')}"</c> do <c>urlparse</c>.</summary>
    public static string NormalizeUrl(string url)
    {
        var rest = StripAsciiControl(url.Trim());
        var hash = rest.IndexOf('#'); if (hash >= 0) rest = rest[..hash];
        var query = rest.IndexOf('?'); if (query >= 0) rest = rest[..query];

        var scheme = "";
        var colon = rest.IndexOf(':');
        if (colon > 0 && char.IsAsciiLetter(rest[0]) && rest[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            scheme = rest[..colon].ToLowerInvariant();
            rest = rest[(colon + 1)..];
        }

        var netloc = "";
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var end = rest.IndexOf('/', 2);
            netloc = end < 0 ? rest[2..] : rest[2..end];
            rest = end < 0 ? "" : rest[end..];
        }
        return $"{scheme}://{Fold(netloc, strip: false)}{rest.TrimEnd('/')}";
    }

    /// <summary>Esquema da URL como o <c>urlparse(url).scheme</c> do Python (minúsculas; vazio se não houver).</summary>
    public static string UrlScheme(string? url)
    {
        var rest = StripAsciiControl((url ?? "").TrimStart());
        var colon = rest.IndexOf(':');
        if (colon > 0 && char.IsAsciiLetter(rest[0]) && rest[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
            return rest[..colon].ToLowerInvariant();
        return "";
    }

    // Python faz casefold() e depois strip(); ToLowerInvariant cobre os textos reais (a diferença é "ß" -> "ss").
    private static string Fold(string? value, bool strip = true)
    {
        var folded = (value ?? "").Replace("ß", "ss", StringComparison.Ordinal).ToLowerInvariant();
        return strip ? folded.Trim() : folded;
    }

    private static string StripAsciiControl(string value) =>
        value.Contains('\t') || value.Contains('\r') || value.Contains('\n')
            ? value.Replace("\t", "").Replace("\r", "").Replace("\n", "")
            : value;
}
