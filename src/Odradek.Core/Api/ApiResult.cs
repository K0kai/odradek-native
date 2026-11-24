using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Odradek.Core.Actions;

namespace Odradek.Core.Api;

/// <summary>Resposta de uma rota da API local, ainda sem HTTP: status, corpo JSON (ou arquivo) e cabeçalhos extras.</summary>
public sealed record ApiResult(int Status, JsonNode? Body = null, byte[]? File = null, string? ContentType = null, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static ApiResult Ok(JsonNode body) => new(200, body);

    /// <summary><c>{"error": mensagem}</c>, o formato de erro das rotas do Qpid e da extensão.</summary>
    public static ApiResult Error(string message, int status = 400) => new(status, new JsonObject { ["error"] = message });

    /// <summary>Erro de validação no formato do FastAPI (422): o que o cliente espera de um parâmetro ou corpo inválido.</summary>
    public static ApiResult Invalid(string where, string name, string message, string type = "value_error") =>
        new(422, new JsonObject { ["detail"] = new JsonArray(new JsonObject { ["type"] = type, ["loc"] = new JsonArray(where, name), ["msg"] = message }) });

    public static ApiResult Pdf(byte[] content) => new(200, null, content, "application/pdf",
        new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"curriculo.pdf\"", ["Cache-Control"] = "no-store" });

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON compacto, como o <c>JSONResponse</c> do Starlette.</summary>
    public string? BodyText => Body?.ToJsonString(Compact);
}

/// <summary>Conversões de linhas do SQLite para JSON, preservando a ordem das colunas (o <c>dict(row)</c> do Python).</summary>
public static class Rows
{
    public static JsonObject ToJson(SqliteDataReader reader)
    {
        var row = new JsonObject();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
            {
                long n => JsonValue.Create(n),
                int n => JsonValue.Create(n),
                double d => JsonValue.Create(d),
                string s => JsonValue.Create(s),
                byte[] b => JsonValue.Create(Convert.ToHexStringLower(b)),
                var other => JsonValue.Create(other.ToString()),
            };
        }
        return row;
    }

    public static List<JsonObject> Query(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<JsonObject>();
        while (reader.Read()) rows.Add(ToJson(reader));
        return rows;
    }
}

/// <summary>Pequenos equivalentes do Python usados nos contratos (<c>json.dumps(sort_keys=True)</c> com SHA-256, <c>==</c> entre tipos JSON).</summary>
public static class Contract
{
    /// <summary><c>sha256(json.dumps(value, sort_keys=True, ensure_ascii=False))</c>.</summary>
    public static string Digest(JsonNode? value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PyJson.Serialize(value, sortKeys: true))));

    public static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary><c>type(x) is int</c>: inteiro JSON (não booleano, sem ponto ou expoente).</summary>
    public static bool IsInt(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue v || v.TryGetValue<bool>(out _) || v.TryGetValue<string>(out _)) return false;
        var text = node.ToJsonString();
        return long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary><c>x == 1</c> do Python: aceita <c>1</c>, <c>1.0</c> e <c>true</c>.</summary>
    public static bool EqualsOne(JsonNode? node)
    {
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var flag)) return flag;
        if (v.TryGetValue<string>(out _)) return false;
        return double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d == 1.0;
    }
}
