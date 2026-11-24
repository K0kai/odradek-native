using System.Text.Json.Nodes;
using Odradek.Core.Actions;

namespace Odradek.Core.Candidate;

/// <summary>Trecho do texto original do PDF: seção, rótulo (do próprio texto) e conteúdo.</summary>
public sealed record ResumeExcerpt(string Section, string Label, string Text);

/// <summary>
/// Trechos do currículo para o Qpid (<c>resume_excerpts</c> de <c>candidate/resume_profile.py</c>): por seção e por cargo, sem cruzar
/// fronteiras de seção. O perfil só ajuda a achar onde cada cargo começa; conteúdo e rótulo vêm do próprio texto.
/// </summary>
public static class ResumeExcerpts
{
    private static readonly string[] BulletStart = ["-", "•", "*", "–", "·", "▪"];

    private static string HeaderLabel(string language) => language == "en" ? "Header" : "Cabeçalho";

    /// <summary>Agrupa linhas inteiras até <paramref name="maxChars"/> pontos de código; só quebra por palavras uma linha maior que o limite.</summary>
    public static List<string> PackLines(IEnumerable<string> lines, int maxChars)
    {
        var pieces = new List<string>();
        var current = "";
        foreach (var original in lines)
        {
            var line = original;
            while (Validate.Length(line) > maxChars)
            {
                var runes = line.EnumerateRunes().Select(r => r.Value).ToArray();
                var cut = Array.LastIndexOf(runes, ' ', maxChars - 1, maxChars); // rfind(" ", 0, max_chars)
                if (cut <= 0) cut = maxChars; // cut > 0 ? cut : max_chars
                if (current.Length > 0) { pieces.Add(current); current = ""; }
                pieces.Add(FromRunes(runes[..cut]).Trim());
                line = FromRunes(runes[cut..]).Trim();
            }
            if (current.Length > 0 && Validate.Length(current) + 1 + Validate.Length(line) > maxChars)
            {
                pieces.Add(current);
                current = "";
            }
            current = current.Length > 0 ? $"{current}\n{line}" : line;
        }
        if (current.Trim().Length > 0) pieces.Add(current);
        return pieces.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static string FromRunes(IEnumerable<int> runes) => string.Concat(runes.Select(r => char.ConvertFromUtf32(r)));

    public static List<ResumeExcerpt> Of(string text, JsonObject? profile = null, int maxChars = 900, string language = "pt")
    {
        var roleTitles = new List<string>();
        foreach (var role in (profile?["roles"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var title = Text.NormalizeText(Text.PyStr(role["title"]));
            if (Validate.Length(title) >= 4) roleTitles.Add(title);
        }

        bool StartsRole(string line)
        {
            var stripped = line.Trim();
            if (stripped.Length == 0 || BulletStart.Any(b => stripped.StartsWith(b, StringComparison.Ordinal)) || Validate.Length(stripped) > 140) return false;
            var norm = Text.NormalizeText(stripped);
            return roleTitles.Any(title => norm.Contains(title, StringComparison.Ordinal));
        }

        var output = new List<ResumeExcerpt>();
        foreach (var section in ResumeProfile.SplitSections(text))
        {
            var lines = Text.SplitLines(section.Text).Select(l => l.Trim()).ToList();
            var heading = section.Kind != "header" && lines.Count > 0 ? lines[0] : "";
            var body = heading.Length > 0 ? lines.Skip(1).ToList() : lines;
            var blocks = new List<List<string>>();
            var current = new List<string>();
            foreach (var line in body)
            {
                if (section.Kind == "experience" && roleTitles.Count > 0 && current.Count > 0 && StartsRole(line))
                {
                    blocks.Add(current);
                    current = [];
                }
                if (line.Length > 0 || current.Count > 0) current.Add(line);
            }
            if (current.Count > 0) blocks.Add(current);
            var title = section.Title.Length > 0 ? section.Title : section.Kind == "header" ? HeaderLabel(language) : section.Kind;
            if (blocks.Count == 0) blocks.Add([]);
            for (var number = 0; number < blocks.Count; number++)
            {
                var block = blocks[number];
                var content = new List<string>();
                if (heading.Length > 0 && number == 0) content.Add(heading);
                content.AddRange(block.Where(l => l.Length > 0));
                if (content.Count == 0) continue;
                var first = block.FirstOrDefault(l => l.Length > 0) ?? "";
                var pieces = PackLines(content, maxChars);
                for (var part = 1; part <= pieces.Count; part++)
                {
                    var label = first.Length > 0 ? $"{title} — {Text.Take(first, 80)}" : title;
                    if (pieces.Count > 1) label += $" ({part}/{pieces.Count})";
                    output.Add(new ResumeExcerpt(section.Kind, label, pieces[part - 1]));
                }
            }
        }
        return output;
    }
}
