using System.Text;

namespace Odradek.Core.Actions;

/// <summary>Erro de validação com texto pronto para o usuário (equivale ao <c>ValueError</c>/HTTP 400 do app Python).</summary>
public sealed class ActionException(string message) : Exception(message);

/// <summary>Resultado de uma ação que pode falhar sem ser um erro de validação (ex.: item da fila em estado errado).</summary>
public sealed record ActionResult(bool Ok, string Notice);

/// <summary>
/// <c>json.dumps(..., ensure_ascii=False)</c> do Python (separadores ", " e ": "). A fila procura trechos como
/// <c>"language": "pt"</c> com <c>LIKE</c>, então o espaçamento precisa ser idêntico ao do app Python.
/// </summary>
public static class PyJson
{
    public static string Dumps(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (key, value) in fields)
        {
            if (!first) sb.Append(", ");
            first = false;
            Quote(sb, key); sb.Append(": ");
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: Quote(sb, s); break;
                case IFormattable n: sb.Append(n.ToString(null, System.Globalization.CultureInfo.InvariantCulture)); break;
                default: Quote(sb, value.ToString() ?? ""); break;
            }
        }
        return sb.Append('}').ToString();
    }

    /// <summary><c>json.dumps(valor, ensure_ascii=False)</c> para qualquer nó JSON: separadores ", " e ": " e a ordem em que as chaves foram inseridas.</summary>
    public static string Serialize(System.Text.Json.Nodes.JsonNode? node, bool sortKeys = false)
    {
        var sb = new StringBuilder();
        Write(sb, node, sortKeys);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, System.Text.Json.Nodes.JsonNode? node, bool sortKeys = false)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case System.Text.Json.Nodes.JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in sortKeys ? o.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList() : o.ToList())
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    Quote(sb, key); sb.Append(": "); Write(sb, value, sortKeys);
                }
                sb.Append('}');
                break;
            case System.Text.Json.Nodes.JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++) { if (i > 0) sb.Append(", "); Write(sb, a[i], sortKeys); }
                sb.Append(']');
                break;
            case System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var s): Quote(sb, s); break;
            case System.Text.Json.Nodes.JsonValue v when v.TryGetValue<bool>(out var flag): sb.Append(flag ? "true" : "false"); break;
            default: sb.Append(node.ToJsonString()); break;
        }
    }

    internal static void Quote(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4")); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}

public static class Validate
{
    /// <summary>Tamanho em pontos de código (o <c>len()</c> do Python), não em unidades UTF-16.</summary>
    public static int Length(string value) => value.EnumerateRunes().Count();

    /// <summary>Só dígitos ASCII, sem sinal (o <c>str.isdigit()</c> do Python aceita mais, o que não importa aqui).</summary>
    public static bool IsDigits(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');

    public static bool InRange(string value, int lower, int upper) =>
        IsDigits(value) && long.TryParse(value, out var n) && n >= lower && n <= upper;
}
