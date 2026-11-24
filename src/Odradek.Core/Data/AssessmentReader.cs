using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odradek.Core.Data;

/// <summary>Versão enxuta da auditoria da triagem v2 (<c>_compact_assessment</c> em <c>web/panel_queries.py</c>), com os mesmos cortes.</summary>
public static class AssessmentReader
{
    public static Assessment? Compact(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(raw); }
        catch (JsonException) { return null; }
        if (parsed is not JsonObject audit) return null;
        if (!Text.Truthy(audit["requirements"]) && !Text.Truthy(audit["responsibilities"])) return null;

        var decision = audit["decision"] as JsonObject ?? [];
        var track = Text.Truthy(audit["track"]) ? Text.PyStr(audit["track"]) : Text.Truthy(decision["track"]) ? Text.PyStr(decision["track"]) : "core";

        return new Assessment(
            Track: track,
            InflatedPosting: Text.Truthy(audit["inflated_posting"]),
            MustCov: Text.PyRound(Number(decision["must_cov"]), 3),
            Threshold: Text.PyRound(Number(decision["threshold"]), 3),
            Blocks: Items(decision["blocks"]).Select(b => Text.Take(Text.PyStrRaw(b), 200)).Take(8).ToList(),
            Requirements: Items(audit["requirements"]).Take(30).Select(Item).ToList(),
            Responsibilities: Items(audit["responsibilities"]).Take(12).Select(Item).ToList(),
            Objections: Items(audit["objections"]).Take(6).Select(o => o as JsonObject ?? []).Select(o => new Objection(
                Severity: o.ContainsKey("severity") ? Text.PyStrRaw(o["severity"]) : "",
                Reason: Text.Take(Text.Truthy(o["reason"]) ? Text.PyStr(o["reason"]) : Text.PyStr(o["job_quote"]), 200))).ToList());
    }

    private static AssessmentItem Item(JsonNode? node)
    {
        var entry = node as JsonObject ?? [];
        string Field(string key) => Text.Take(Text.PyStr(entry[key]), 240);
        return new AssessmentItem(Field("id"), Field("text"), Field("kind"), Field("status"), Field("realism"),
            Field("realism_reason"), Field("bridge"), Field("resume_quote"), Field("job_quote"), Field("note"),
            YearsRequired: Number(entry["years_required"]), ProvenMonths: (int)Number(entry["proven_months"]), Category: Field("category"));
    }

    private static IEnumerable<JsonNode?> Items(JsonNode? node) => node is JsonArray array ? array : [];

    private static double Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var d) ? d : node is JsonValue v2 && v2.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
