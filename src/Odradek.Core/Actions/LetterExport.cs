using System.Globalization;
using System.Text;
using Dapper;
using Odradek.Core.Data;

namespace Odradek.Core.Actions;

/// <summary>
/// Exporta a carta de apresentação em TXT (UTF-8) ou PDF. O PDF é gerado à mão (A4, Helvetica, margens de 54 pt, corpo 11/16 pt, quebra de linha
/// e paginação) para não depender de pacote novo. Acentos do português e a pontuação comum vão em WinAnsi; o que não couber vira "?".
/// </summary>
public static class LetterExport
{
    public static string? LoadBody(Database db, long jobId)
    {
        using var connection = db.Open();
        var body = connection.QueryFirstOrDefault<string?>("SELECT body FROM cover_letters WHERE job_id=@jobId", new { jobId });
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    public static byte[] Txt(string body) => new UTF8Encoding(false).GetBytes(body);

    // Larguras da Helvetica (1/1000 de em) para ASCII 32..126; letras acentuadas usam a largura da letra base.
    private static readonly int[] Widths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
        278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667,
        611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833,
        556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly Dictionary<char, byte> WinAnsiExtras = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A,
        ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97,
        ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    private static byte Encode(char c)
    {
        if (c is >= ' ' and <= '~') return (byte)c;
        if (c is >= ' ' and <= 'ÿ') return (byte)c;
        if (c == ' ') return 0x20;
        return WinAnsiExtras.TryGetValue(c, out var b) ? b : (byte)'?';
    }

    private static readonly Dictionary<char, char> BaseLetter = BuildBaseLetters();

    private static Dictionary<char, char> BuildBaseLetters()
    {
        var map = new Dictionary<char, char>();
        for (var c = 'À'; c <= 'ÿ'; c++)
        {
            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            if (decomposed.Length > 1 && decomposed[0] < 128) map[c] = decomposed[0];
        }
        return map;
    }

    private static double Width(char c, double size)
    {
        var ch = BaseLetter.TryGetValue(c, out var b) ? b : c;
        var units = ch is >= ' ' and <= '~' ? Widths[ch - ' '] : 556;
        return units * size / 1000.0;
    }

    private static List<string> WrapLine(string line, double size, double maxWidth)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        double width = 0;
        foreach (var word in line.Split(' '))
        {
            var wordWidth = word.Sum(c => Width(c, size));
            var space = current.Length > 0 ? Width(' ', size) : 0;
            if (current.Length > 0 && width + space + wordWidth > maxWidth) { lines.Add(current.ToString()); current.Clear(); width = 0; space = 0; }
            if (wordWidth > maxWidth) // palavra maior que a linha: quebra por caracteres
            {
                foreach (var c in word)
                {
                    var w = Width(c, size);
                    if (width + w > maxWidth && current.Length > 0) { lines.Add(current.ToString()); current.Clear(); width = 0; }
                    current.Append(c); width += w;
                }
                continue;
            }
            if (current.Length > 0) { current.Append(' '); width += space; }
            current.Append(word); width += wordWidth;
        }
        lines.Add(current.ToString());
        return lines;
    }

    private static void Escape(StringBuilder sb, string text)
    {
        foreach (var c in text)
        {
            var b = Encode(c);
            if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else if (b is < 32 or > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
    }

    public static byte[] Pdf(string body, string title = "Cover letter")
    {
        const double pageWidth = 595.28, pageHeight = 841.89, margin = 54, size = 11, leading = 16, blank = 12;
        var pages = new List<StringBuilder>();
        StringBuilder? page = null;
        double y = 0;
        void NewPage()
        {
            page = new StringBuilder();
            pages.Add(page);
            y = pageHeight - margin - size;
        }
        NewPage();
        foreach (var line in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                y -= blank;
                if (y < margin) NewPage();
                continue;
            }
            foreach (var piece in WrapLine(line.Replace('\t', ' '), size, pageWidth - 2 * margin))
            {
                if (y < margin) NewPage();
                page!.Append("BT /F1 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" Tf ")
                    .Append(margin.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(y.ToString("0.##", CultureInfo.InvariantCulture)).Append(" Td (");
                Escape(page, piece);
                page.Append(") Tj ET\n");
                y -= leading;
            }
        }

        // objetos: 1 catálogo, 2 páginas, 3 fonte, 4 info, depois (página, conteúdo) por página
        var objects = new List<string>();
        var kids = string.Join(' ', Enumerable.Range(0, pages.Count).Select(i => $"{5 + 2 * i} 0 R"));
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var info = new StringBuilder("<< /Title (");
        Escape(info, title);
        objects.Add(info.Append(") >>").ToString());
        for (var i = 0; i < pages.Count; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth.ToString(CultureInfo.InvariantCulture)} {pageHeight.ToString(CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 3 0 R >> >> /Contents {6 + 2 * i} 0 R >>");
            var content = pages[i].ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        using var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
