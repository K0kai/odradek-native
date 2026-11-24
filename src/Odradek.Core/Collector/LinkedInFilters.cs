using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odradek.Core.Actions;

namespace Odradek.Core.Collector;

/// <summary>Filtros de busca do LinkedIn guardados em <c>settings</c> (<c>app.py:1250-1372</c>): normalização, validação do formulário e leitura.</summary>
public static class LinkedInFilters
{
    public const int MaxKeywords = 2;
    public const string MainKey = "linkedin_main_filter_json";
    public const string SecondaryKey = "linkedin_secondary_filter_json";

    public static string KeyFor(string slot) => slot == "main" ? MainKey : SecondaryKey;

    private static string Prefix(string slot) => slot == "main" ? "main-" : "sec-";

    private static string S(JsonNode? node) => Text.PyStr(node);

    private static List<int> Levels(JsonNode? raw)
    {
        var levels = new List<int>();
        if (raw is not JsonArray list) return levels;
        foreach (var item in list)
        {
            var code = ApifyActors.PyInt(item);
            if (code is { } c && LinkedInSearch.ExperienceCodes.Contains((int)c) && c is >= int.MinValue and <= int.MaxValue && !levels.Contains((int)c)) levels.Add((int)c);
        }
        return levels;
    }

    private static string Sha10(string signature) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant()[..10];

    /// <summary>Um filtro: 1–2 frases do MESMO cargo, níveis de experiência e metadados verificados; <c>null</c> se não sobrar frase.</summary>
    public static JsonObject? Normalize(JsonNode? raw, string slot)
    {
        if (raw is not JsonObject source) return null;
        var keywords = new List<string>();
        foreach (var entry in source["keywords"] as JsonArray ?? [])
        {
            var phrase = Text.Take(S(entry is JsonObject e ? e["phrase"] : entry).Trim(), 120);
            if (phrase.Length > 0 && !keywords.Any(k => string.Equals(k, phrase, StringComparison.OrdinalIgnoreCase))) keywords.Add(phrase);
        }
        keywords = keywords.Take(MaxKeywords).ToList();
        if (keywords.Count == 0) return null;
        var levels = Levels(source["experience_levels"]);
        var signature = new StringBuilder("{\"keywords\": [");
        signature.Append(string.Join(", ", keywords.Select(k => PyJson.Serialize(JsonValue.Create(k)))));
        signature.Append("], \"levels\": [").Append(string.Join(", ", levels)).Append("]}");
        var output = new JsonObject
        {
            ["id"] = Prefix(slot) + Sha10(signature.ToString()),
            ["name"] = Text.Take(S(source["name"]).Trim(), 80) is { Length: > 0 } name ? name : keywords[0],
            ["slot"] = slot,
            ["enabled"] = slot == "main" || !source.ContainsKey("enabled") || Text.Truthy(source["enabled"]),
            ["keywords"] = new JsonArray(keywords.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["experience_levels"] = new JsonArray(levels.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
            ["reason"] = Text.Take(S(source["reason"]).Trim(), 400),
            ["source"] = Text.Take(Text.Truthy(source["source"]) ? S(source["source"]) : "manual", 20),
        };
        if (slot == "secondary")
        {
            output["track"] = "transferable";
            foreach (var key in new[] { "sector", "resilience_reason" })
                if (S(source[key]).Trim().Length > 0) output[key] = Text.Take(S(source[key]).Trim(), 400);
        }
        if (source["evidence"] is JsonObject evidence)
        {
            var kept = new JsonObject();
            foreach (var phrase in keywords)
                if (evidence[phrase] is JsonArray ids)
                    kept[phrase] = new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(Text.Take(Text.PyStrRaw(i), 12))).Take(4).ToArray());
            output["evidence"] = kept;
        }
        var anchors = new JsonArray();
        foreach (var item in source["anchor_roles"] as JsonArray ?? [])
        {
            if (item is not JsonObject role || S(role["id"]).Trim().Length == 0) continue;
            anchors.Add(new JsonObject
            {
                ["id"] = Text.Take(Text.PyStrRaw(role["id"]), 12), ["title"] = Text.Take(S(role["title"]), 160),
                ["employer"] = Text.Take(S(role["employer"]), 160), ["months"] = Text.Truthy(role["months"]) ? (int)(ApifyActors.PyInt(role["months"]) ?? 0) : 0,
            });
        }
        if (anchors.Count > 0) output["anchor_roles"] = new JsonArray(anchors.Take(6).Select(a => a!.DeepClone()).ToArray());
        var months = Text.Truthy(source["career_months"]) ? ApifyActors.PyInt(source["career_months"]) : 0;
        if (months is { } m) output["career_months"] = (int)Math.Max(0, m);
        if (Text.Truthy(source["generated_at"])) output["generated_at"] = Text.Take(Text.PyStrRaw(source["generated_at"]), 40);
        return output;
    }

    /// <summary>Valida o JSON salvo pelo painel: vazio (sem filtro) ou UM objeto com 1–2 frases. Devolve o JSON normalizado.</summary>
    public static string ValidateForm(string? raw, string slot)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "";
        JsonNode? data;
        try { data = JsonNode.Parse(text); }
        catch (JsonException ex) { throw new ActionException($"JSON do filtro inválido: {ex.Message}"); }
        if (data is JsonArray) throw new ActionException("Use um único filtro (objeto), não uma lista.");
        if (data is not JsonObject obj) throw new ActionException("Filtro inválido.");
        if (S(obj["name"]).Trim().Length == 0) throw new ActionException("Dê um nome ao filtro.");
        var distinct = new HashSet<string>();
        foreach (var entry in obj["keywords"] as JsonArray ?? [])
        {
            // Python: str(entry.get("phrase") if dict else entry or ""): frase ausente num objeto vira "None".
            var phrase = entry is JsonObject e ? Text.PyStrRaw(e["phrase"]) : S(entry);
            var key = phrase.Trim().ToLowerInvariant();
            if (key.Length > 0) distinct.Add(key);
        }
        if (distinct.Count == 0) throw new ActionException("Adicione ao menos uma keyword ao filtro.");
        if (distinct.Count > MaxKeywords)
            throw new ActionException($"Use no máximo {MaxKeywords} frases por filtro: o cargo e uma variante equivalente dele.");
        return PyJson.Serialize(Normalize(obj, slot));
    }

    /// <summary>Filtro salvo (nunca gerado sozinho: só por ação explícita). <c>null</c> se vazio ou inválido (este caso é avisado no log).</summary>
    public static JsonObject? Load(IReadOnlyDictionary<string, string> cfg, string slot, Action<string, string>? log = null)
    {
        var raw = (cfg.GetValueOrDefault(KeyFor(slot)) ?? "").Trim();
        if (raw.Length == 0) return null;
        JsonObject? item;
        try { item = Normalize(JsonNode.Parse(raw), slot); }
        catch (JsonException) { item = null; }
        if (item is null) log?.Invoke("warning", $"Filtro {(slot == "main" ? "principal" : "secundário")} salvo é inválido; ignorado.");
        return item;
    }
}
