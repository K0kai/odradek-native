using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odradek.Core.Actions;

namespace Odradek.Core.Collector;

/// <summary>Actors do Apify configurados no painel e a entrada de cada execução (porte de <c>app.py:815-1040, 1745-1788</c>).</summary>
public static class ApifyActors
{
    public const string Api = "https://api.apify.com/v2";

    private static string S(JsonNode? node) => Text.PyStr(node);

    public static List<JsonObject> Defaults() =>
    [
        new JsonObject
        {
            ["id"] = "curious_coder~linkedin-jobs-scraper", ["label"] = "LinkedIn Jobs", ["enabled"] = true, ["input_mode"] = "linkedin_search", ["count"] = 25,
        },
    ];

    /// <summary><c>owner/name</c> vira <c>owner~name</c>, o formato da API.</summary>
    public static string NormalizeId(string? actorId)
    {
        var raw = (actorId ?? "").Trim();
        if (raw.Contains('/') && !raw.Contains('~'))
        {
            var parts = raw.Split('/', 2);
            return $"{parts[0].Trim()}~{parts[1].Trim()}";
        }
        return raw;
    }

    /// <summary><c>int(x)</c> do Python para texto ou número JSON; <c>null</c> onde o Python levantaria erro.</summary>
    internal static long? PyInt(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
            if (v.TryGetValue<string>(out var s)) return long.TryParse(s.Trim().Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
            if (double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d)) return (long)Math.Truncate(d);
        }
        return null;
    }

    public static List<JsonObject> Load(IReadOnlyDictionary<string, string> cfg)
    {
        var raw = (cfg.GetValueOrDefault("apify_actors_json") ?? "").Trim();
        if (raw.Length == 0) return Defaults();
        JsonNode? data;
        try { data = JsonNode.Parse(raw); }
        catch (JsonException ex) { throw new ActionException($"JSON de actors Apify inválido: {ex.Message}"); }
        if (data is not JsonArray list || list.Count == 0) throw new ActionException("apify_actors_json deve ser uma lista JSON com pelo menos um actor.");
        var actors = new List<JsonObject>();
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] is not JsonObject item) throw new ActionException($"Actor Apify #{index + 1} deve ser um objeto JSON.");
            var actorId = NormalizeId(S(item["id"]));
            if (actorId.Length == 0) throw new ActionException($"Actor Apify #{index + 1} sem id.");
            var label = S(item["label"]).Trim();
            if (label.Length == 0) label = actorId;
            var enabled = item["enabled"] is null || Text.Truthy(item["enabled"]);
            var mode = S(item["input_mode"]).Trim().ToLowerInvariant();
            if (mode.Length == 0) mode = "linkedin_search";
            if (mode is not ("linkedin_search" or "custom"))
                mode = Text.Truthy(item["input_template"]) || Text.Truthy(item["input"]) ? "custom" : "linkedin_search";
            var countSource = Text.Truthy(item["count"]) ? item["count"] : JsonValue.Create(cfg.GetValueOrDefault("apify_job_count") is { Length: > 0 } c ? c : "25");
            var count = PyInt(countSource) is { } parsed ? (int)Math.Max(1, Math.Min(100, parsed)) : 25;
            var template = item.ContainsKey("input_template") ? item["input_template"] : item["input"];
            actors.Add(new JsonObject
            {
                ["id"] = actorId, ["label"] = label, ["enabled"] = enabled, ["input_mode"] = mode, ["count"] = count,
                ["input_template"] = template is JsonObject or JsonArray ? template.DeepClone() : null,
            });
        }
        return actors;
    }

    public static JsonNode? Substitute(JsonNode? value, IReadOnlyDictionary<string, string> context)
    {
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                foreach (var (key, replacement) in context) text = text.Replace("{{" + key + "}}", replacement, StringComparison.Ordinal);
                return text;
            case JsonArray a: return new JsonArray(a.Select(item => Substitute(item, context)).ToArray());
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, item) in o) copy[key] = Substitute(item, context);
                return copy;
            default: return value?.DeepClone();
        }
    }

    /// <summary>Entrada da execução do actor: URLs do LinkedIn (modo <c>linkedin_search</c>) ou o modelo do actor com os placeholders preenchidos.</summary>
    public static JsonObject BuildRunInput(JsonObject actor, IReadOnlyDictionary<string, string> cfg, JsonObject? linkedinFilter = null,
        List<string>? linkedinUrls = null, Func<JsonObject?>? mainFilter = null)
    {
        var keywords = LinkedInSearch.Terms(cfg.GetValueOrDefault("keywords")).Take(3).ToList();
        if (keywords.Count == 0) keywords = ["software engineer"];
        var locations = LinkedInSearch.Terms(cfg.GetValueOrDefault("locations")).Take(3).ToList();
        if (locations.Count == 0) locations = ["remote"];
        var count = Text.Truthy(actor["count"]) ? (int)(PyInt(actor["count"]) ?? 25) : 25;
        var mode = Text.Truthy(actor["input_mode"]) ? S(actor["input_mode"]) : "linkedin_search";
        if (mode == "linkedin_search")
        {
            var filter = linkedinFilter ?? mainFilter?.Invoke() ?? throw new ActionException("Gere o filtro Principal no painel antes de coletar no LinkedIn.");
            var urls = linkedinUrls ?? LinkedInSearch.BuildSearchUrls(filter, LinkedInSearch.SearchScope(cfg), LinkedInSearch.MaxAgeDays(cfg));
            return new JsonObject { ["urls"] = new JsonArray(urls.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()), ["count"] = count, ["scrapeCompany"] = false };
        }
        var template = actor["input_template"];
        if (template is not (JsonObject or JsonArray))
            throw new ActionException($"Actor {S(actor["label"])} em modo custom precisa de input_template (objeto JSON).");
        var context = new Dictionary<string, string>
        {
            ["count"] = count.ToString(CultureInfo.InvariantCulture), ["keywords"] = string.Join(", ", keywords), ["locations"] = string.Join(", ", locations),
            ["keyword"] = keywords[0], ["location"] = locations[0], ["keyword0"] = keywords[0], ["keyword1"] = keywords.Count > 1 ? keywords[1] : keywords[0],
            ["location0"] = locations[0], ["location1"] = locations.Count > 1 ? locations[1] : locations[0],
            ["max_age_days"] = LinkedInSearch.MaxAgeDays(cfg).ToString(CultureInfo.InvariantCulture),
        };
        if (actor["_ctx"] is JsonObject extra) foreach (var (key, value) in extra) context[key] = S(value); // consulta específica planejada para esta execução
        var built = Substitute(template, context);
        if (built is JsonArray array) return new JsonObject { ["items"] = array };
        if (built is JsonObject obj) return obj;
        throw new ActionException($"input_template do actor {S(actor["label"])} deve resultar em objeto JSON.");
    }
}
