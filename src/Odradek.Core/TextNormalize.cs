using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Odradek.Core;

public static partial class Text
{
    [GeneratedRegex("[^a-z0-9+#]+")]
    private static partial Regex NotAlnum();

    /// <summary><c>normalize_text</c> (<c>candidate/resume_profile.py:110</c>): minúsculas, sem acentos, só <c>[a-z0-9+#]</c> separados por um espaço.</summary>
    public static string NormalizeText(string? value)
    {
        var folded = (value ?? "").Replace("ß", "ss", StringComparison.Ordinal).ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var stripped = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            stripped.Append(ch);
        }
        return NotAlnum().Replace(stripped.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"<\s*(?:br|/p|/li|/div|/h[1-6]|/tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex("[ \\t\\u00a0]+")]
    private static partial Regex Blanks();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();

    /// <summary><c>clean_job_html</c> (<c>jobs/job_assessment.py:139</c>): HTML vira texto, preservando as quebras de bloco.</summary>
    public static string CleanJobHtml(string? raw)
    {
        var text = BlockBreak().Replace(raw ?? "", "\n");
        text = ListItem().Replace(text, "\n- ");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Blanks().Replace(text, " ");
        return BlankLines().Replace(text, "\n").Trim();
    }
}
