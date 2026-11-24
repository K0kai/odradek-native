using System.Text.Json.Nodes;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Candidate;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.Core.Engines;

/// <summary>
/// Gera os filtros de busca do LinkedIn a partir do perfil verificado do currículo (<c>app.py:1170-1250, 1374-1506</c>).
/// A IA propõe e cita evidências; o código confere cada frase contra o perfil antes de aceitar.
/// </summary>
public sealed class FilterGenerator(Database db, AiClient ai, Func<string, string>? secret = null)
{
    private static readonly HashSet<string> TechCategories = ["language", "framework", "tool", "cloud", "db"];

    private static string S(JsonNode? node) => Text.PyStr(node);

    public static string EffectiveMainPrompt(IReadOnlyDictionary<string, string>? cfg)
    {
        var custom = (cfg?.GetValueOrDefault("linkedin_filter_prompt") ?? "").Trim();
        return custom.Length > 0 ? custom : FilterPrompts.DefaultMain;
    }

    public static string EffectiveSecondaryPrompt(IReadOnlyDictionary<string, string>? cfg)
    {
        var custom = (cfg?.GetValueOrDefault("transferable_prompt") ?? "").Trim();
        return custom.Length > 0 ? custom : FilterPrompts.DefaultSecondary;
    }

    public static JsonObject Schema() => JsonNode.Parse("""
        {"type":"object","properties":{"filter":{"type":"object","properties":{
          "name":{"type":"string"},
          "keywords":{"type":"array","items":{"type":"object","properties":{"phrase":{"type":"string"},"evidence_ids":{"type":"array","items":{"type":"string"}}}}},
          "anchor_role_ids":{"type":"array","items":{"type":"string"}},
          "experience_levels":{"type":"array","items":{"type":"integer"}},
          "reason":{"type":"string"},"sector":{"type":"string"},"resilience_reason":{"type":"string"}}}}}
        """)!.AsObject();

    /// <summary>Substitui os marcadores sem formatar (o modelo pode ter chaves JSON). Sem marcador de perfil, o perfil vai no fim.</summary>
    public static string RenderPrompt(string? template, string prefKeywords, string factsPt, string factsEn, string resumePt = "", string resumeEn = "",
        string profile = "", string areaHint = "", string mainFilter = "")
    {
        static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
        var pairs = new (string Key, string Value)[]
        {
            ("pref_keywords", Or(prefKeywords, "[none]")), ("facts_pt", Or(factsPt, "[none]")), ("facts_en", Or(factsEn, "[none]")),
            ("resume_pt", Or(resumePt, Or(profile, "[none]"))),
            ("resume_en", Or(resumeEn, resumePt.Length > 0 || profile.Length > 0 ? "(PT and EN resumes are merged in the PT block above)" : "[none]")),
            ("profile", Or(profile, "[none]")), ("area_hint", Or(areaHint, "[none: choose from the profile]")), ("main_filter", Or(mainFilter, "[not generated yet]")),
        };
        var output = string.IsNullOrEmpty(template) ? FilterPrompts.DefaultMain : template;
        var carriesProfile = new[] { "profile", "resume_pt", "resume_en" }.Any(key => output.Contains("{" + key + "}", StringComparison.Ordinal));
        foreach (var (key, value) in pairs) output = output.Replace("{" + key + "}", value, StringComparison.Ordinal);
        if (!carriesProfile && profile.Length > 0) output += $"\n\nVERIFIED CANDIDATE PROFILE (evidence ids in brackets):\n{profile}\n";
        return output;
    }

    /// <summary>Confere o filtro proposto pela IA contra o perfil verificado; lança <see cref="ActionException"/> se nada sobrar.</summary>
    public static JsonObject Verify(JsonObject raw, JsonObject profile, string slot, IReadOnlyCollection<string> avoid, Action<string, string> log, string? nowIso = null)
    {
        var index = ResumeProfile.EvidenceIndex(profile);
        var derived = profile["derived"] as JsonObject ?? new JsonObject();
        var roles = new Dictionary<string, JsonObject>();
        foreach (var role in (profile["roles"] as JsonArray ?? []).OfType<JsonObject>()) roles[Text.PyStrRaw(role["id"])] = role;
        var avoidKeys = avoid.Select(a => a.ToLowerInvariant()).ToHashSet();
        var wantedKinds = slot == "main" ? new HashSet<string> { "role", "responsibility" } : ["transferable", "role", "responsibility"];

        string NamedTechnology(string phrase)
        {
            var norm = $" {Text.NormalizeText(phrase)} ";
            foreach (var skill in (profile["skills"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!TechCategories.Contains(S(skill["category"]))) continue;
                var name = Text.NormalizeText(S(skill["name"]));
                if (name.Length > 0 && norm.Contains($" {name} ", StringComparison.Ordinal)) return Text.PyStrRaw(skill["name"]);
            }
            return "";
        }

        var kept = new List<string>();
        var evidence = new JsonObject();
        var anchorIds = new List<string>();
        foreach (var entry in raw["keywords"] as JsonArray ?? [])
        {
            var phrase = S(entry is JsonObject e ? e["phrase"] : entry).Trim();
            var ids = (entry is JsonObject e2 ? e2["evidence_ids"] as JsonArray : null)?.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null).Where(s => s is not null && index.ContainsKey(s)).Select(s => s!).ToList() ?? [];
            if (phrase.Length == 0) continue;
            string reason = "";
            if (ids.Count == 0) reason = "sem evidência válida no perfil";
            else if (avoidKeys.Contains(phrase.ToLowerInvariant())) reason = "repete o filtro principal";
            else if (!ids.Any(i => wantedKinds.Contains(index[i].Kind)))
                reason = slot == "main" ? "não cita um cargo do currículo" : "não cita capacidade transferível nem cargo";
            else if (NamedTechnology(phrase) is { Length: > 0 } skill) reason = $"cita a tecnologia '{skill}'; o filtro é pelo cargo, não pela stack";
            if (reason.Length > 0)
            {
                log("info", $"Keyword descartada ({reason}): {phrase}");
                continue;
            }
            if (kept.Any(k => string.Equals(k, phrase, StringComparison.OrdinalIgnoreCase))) continue;
            kept.Add(phrase);
            evidence[phrase] = new JsonArray(ids.Take(4).Select(i => (JsonNode?)i).ToArray());
            foreach (var id in ids)
                if (index[id].Kind is "role" or "responsibility") anchorIds.Add(id.Split('.')[0]);
            if (kept.Count == LinkedInFilters.MaxKeywords) break;
        }
        if (kept.Count == 0)
            throw new ActionException("A IA não retornou uma frase com evidência válida no currículo "
                + (slot == "main" ? "(o filtro principal precisa citar um cargo seu)." : "(capacidade transferível ou cargo)."));
        foreach (var id in raw["anchor_role_ids"] as JsonArray ?? [])
            if (id is JsonValue v && v.TryGetValue<string>(out var s) && roles.ContainsKey(s)) anchorIds.Add(s);
        var anchors = new List<string>();
        foreach (var id in anchorIds)
            if (roles.ContainsKey(id) && !anchors.Contains(id)) anchors.Add(id);
        var roleMonths = derived["role_months"] as JsonObject ?? new JsonObject();
        var output = new JsonObject
        {
            ["name"] = raw["name"]?.DeepClone(),
            ["keywords"] = new JsonArray(kept.Select(k => (JsonNode?)k).ToArray()),
            ["experience_levels"] = raw["experience_levels"]?.DeepClone(),
            ["reason"] = raw["reason"]?.DeepClone(),
            ["evidence"] = evidence,
            ["source"] = "ai",
            ["generated_at"] = nowIso ?? Clock.NowIso(),
            ["anchor_roles"] = new JsonArray(anchors.Select(id => (JsonNode?)new JsonObject
            {
                ["id"] = id, ["title"] = roles[id]["title"] is { } t ? t.DeepClone() : "", ["employer"] = roles[id]["employer"] is { } em ? em.DeepClone() : "",
                ["months"] = roleMonths[id]?.DeepClone() ?? 0,
            }).ToArray()),
            ["career_months"] = ResumeProfile.MonthsForRoles(profile, anchors),
            ["enabled"] = true,
        };
        if (slot == "secondary") { output["sector"] = raw["sector"]?.DeepClone(); output["resilience_reason"] = raw["resilience_reason"]?.DeepClone(); }
        return output;
    }

    private static readonly string OutputInstruction =
        "\n\nOUTPUT: JSON {\"filter\": {...}} with EXACTLY ONE filter: name (short, Portuguese), "
        + "keywords (1-2 objects {\"phrase\", \"evidence_ids\"} naming the SAME role), anchor_role_ids, "
        + "experience_levels, reason (Portuguese), sector and resilience_reason (empty strings for the main filter).";

    /// <summary>Gera UM filtro (principal ou secundário) a partir do perfil verificado dos currículos.</summary>
    public async Task<JsonObject> GenerateAsync(IReadOnlyDictionary<string, string> cfg, string slot, CancellationToken ct = default)
    {
        var bundle = ResumeData.MergedProfile(db, "pt");
        if (bundle is null)
            throw new ActionException("Analise um currículo antes de gerar o filtro LinkedIn/Apify "
                + "(se já analisou, aguarde a reanálise do perfil verificado na fila).");
        return await GenerateWithAsync(bundle.Value.Profile, cfg, slot, ct).ConfigureAwait(false);
    }

    internal async Task<JsonObject> GenerateWithAsync(JsonObject profile, IReadOnlyDictionary<string, string> cfg, string slot, CancellationToken ct = default, string? nowIso = null)
    {
        var provider = (cfg.GetValueOrDefault("ai_provider") is { Length: > 0 } p ? p : "gemini").ToLowerInvariant();
        var model = (cfg.GetValueOrDefault("ai_model") is { Length: > 0 } m ? m : "gemini-3.5-flash-lite").Trim();
        var apiKey = (secret ?? (name => SecretStore.Get(name)))(provider);
        if (apiKey.Length == 0) throw new AiUnavailableException("Configure a chave da API de IA para gerar o filtro LinkedIn.");
        var log = new EventLogStore(db);
        void Log(string level, string message) => log.Add(level, "apify", message);
        var isSecondary = slot == "secondary";
        var main = isSecondary ? LinkedInFilters.Load(cfg, "main", Log) : null;
        var mainKeywords = main?["keywords"] is JsonArray list ? list.Select(k => Text.PyStrRaw(k)).ToList() : [];
        var prompt = RenderPrompt(isSecondary ? EffectiveSecondaryPrompt(cfg) : EffectiveMainPrompt(cfg),
            cfg.GetValueOrDefault("keywords") ?? "", (cfg.GetValueOrDefault("candidate_facts_pt") ?? "").Trim(), (cfg.GetValueOrDefault("candidate_facts_en") ?? "").Trim(),
            profile: ResumeProfile.CompactProfileText(profile, 9500), areaHint: (cfg.GetValueOrDefault("secondary_area_hint") ?? "").Trim(),
            mainFilter: string.Join(" / ", mainKeywords)) + OutputInstruction;
        var decision = await ai.JsonAsync(prompt, Schema(), new AiCall(provider, model, apiKey), maxOutputTokens: 3000, thinkingBudget: 1024, ct: ct).ConfigureAwait(false);
        var raw = decision["filter"] as JsonObject ?? new JsonObject();
        var verified = Verify(raw, profile, slot, mainKeywords, Log, nowIso);
        return LinkedInFilters.Normalize(verified, slot) ?? throw new ActionException("A IA não retornou um filtro utilizável.");
    }

    /// <summary>Salva as edições da busca e substitui APENAS o filtro do slot pedido, por ação explícita do usuário.</summary>
    public async Task<JsonObject> GenerateAndSaveAsync(IReadOnlyDictionary<string, string> form, string slot, CancellationToken ct = default)
    {
        new SettingsActions(db).Save(form);
        var store = new SettingsStore(db);
        var generated = await GenerateAsync(store.All(), slot, ct).ConfigureAwait(false);
        store.Set(LinkedInFilters.KeyFor(slot), PyJson.Serialize(generated));
        new EventLogStore(db).Add("info", "apify",
            $"IA gerou o filtro {(slot == "main" ? "Principal" : "Secundário")}: {string.Join(" / ", (generated["keywords"] as JsonArray ?? []).Select(k => Text.PyStrRaw(k)))}.");
        return generated;
    }
}
