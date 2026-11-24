using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Odradek.Core;

/// <summary>Pequenos equivalentes do comportamento do Python de que as telas e as consultas dependem (fatias por ponto de código, <c>str()</c>, <c>splitlines</c>).</summary>
public static partial class Text
{
    /// <summary>Equivale a <c>s[:n]</c> do Python: conta pontos de código (um emoji vale 1), não unidades UTF-16.</summary>
    public static string Take(string? value, int count)
    {
        if (string.IsNullOrEmpty(value) || count <= 0) return "";
        if (value.Length <= count) return value; // atalho: UTF-16 nunca tem menos unidades do que pontos de código
        var taken = 0;
        var i = 0;
        while (i < value.Length && taken < count)
        {
            i += char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
            taken++;
        }
        return value[..i];
    }

    /// <summary>Equivale a <c>s[-n:]</c>.</summary>
    public static string TakeLast(string? value, int count)
    {
        if (string.IsNullOrEmpty(value) || count <= 0) return "";
        var runes = value.EnumerateRunes().ToList();
        return runes.Count <= count ? value : string.Concat(runes.Skip(runes.Count - count).Select(r => r.ToString()));
    }

    /// <summary>Equivale a <c>str(x or "")</c> para um valor JSON: vazio quando o valor é "falso" em Python.</summary>
    public static string PyStr(JsonNode? node) => Truthy(node) ? PyStrRaw(node) : "";

    /// <summary>Equivale a <c>str(x)</c>: <c>None</c>, <c>True</c>/<c>False</c>, inteiros e floats como o Python escreve.</summary>
    public static string PyStrRaw(JsonNode? node)
    {
        if (node is null) return "None";
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<bool>(out var b)) return b ? "True" : "False";
            var raw = value.ToJsonString();
            if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0) return raw; // inteiro: o texto original já é o do Python
            var d = value.GetValue<double>();
            if (d == Math.Floor(d) && Math.Abs(d) < 1e16) return d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
        return node.ToJsonString(); // lista/objeto: o repr do Python não é reproduzido (não aparece nos dados reais)
    }

    /// <summary>
    /// Equivale a <c>round(x, digits)</c> do Python: decide pelo valor binário exato do <c>double</c> (0.1235 vira 0.123, 0.0005 vira 0.001)
    /// e, só num empate exato, arredonda para o par. <c>Math.Round</c> escala por 10ⁿ e erra nesses casos.
    /// </summary>
    public static double PyRound(double x, int digits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var expanded = Math.Abs(x).ToString("F60", inv); // expansão decimal exata (.NET Core 3.0+)
        var dot = expanded.IndexOf('.');
        var whole = expanded[..dot];
        var fraction = expanded[(dot + 1)..];
        var kept = fraction[..digits];
        var rest = fraction[digits..];
        var up = rest[0] > '5' || (rest[0] == '5' && (rest.AsSpan(1).IndexOfAnyExcept('0') >= 0 || (kept.Length > 0 ? (kept[^1] - '0') : (whole[^1] - '0')) % 2 == 1));
        var scaled = System.Numerics.BigInteger.Parse(whole + kept, inv) + (up ? 1 : 0);
        var text = scaled.ToString(inv).PadLeft(digits + 1, '0');
        var result = double.Parse(text[..^digits] + "." + text[^digits..], inv);
        return x < 0 ? -result : result;
    }

    /// <summary>Verdade em Python: não nulo, não vazio, não zero, não falso.</summary>
    public static bool Truthy(JsonNode? node)
    {
        switch (node)
        {
            case null: return false;
            case JsonArray array: return array.Count > 0;
            case JsonObject obj: return obj.Count > 0;
            case JsonValue value:
                if (value.TryGetValue<string>(out var s)) return s.Length > 0;
                if (value.TryGetValue<bool>(out var b)) return b;
                // TryGetValue<double> falha em números criados em C# (int); o texto JSON serve para os dois casos.
                return double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d != 0;
            default: return false;
        }
    }

    private static bool IsLineBreak(char c) =>
        c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\x2028' or '\x2029';

    /// <summary>Equivale a <c>str.splitlines()</c>: quebra em \n, \r\n, \r e nos separadores Unicode, sem linha vazia no fim.</summary>
    public static List<string> SplitLines(string? value)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(value)) return lines;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!IsLineBreak(c)) continue;
            lines.Add(value[start..i]);
            if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < value.Length) lines.Add(value[start..]);
        return lines;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    /// <summary>Equivale a <c>re.sub(r"&lt;[^&gt;]+&gt;", " ", html)</c>.</summary>
    public static string StripTags(string? html) => TagRegex().Replace(html ?? "", " ");
}
