using System.Text;
using System.Text.Json.Nodes;
using Odradek.Core.Candidate;

namespace Odradek.Core.Collector;

/// <summary>Um par (local, modelo de trabalho) das buscas do LinkedIn.</summary>
public sealed record ScopeItem(string Location, string FWt, string Label);

/// <summary>Uma execução planejada do Apify: o actor (já com a contagem do lote), o filtro de origem e as URLs de busca (LinkedIn).</summary>
public sealed record PlannedRun(JsonObject Actor, JsonObject? Filter, List<string>? Urls);

/// <summary>Buscas do LinkedIn e planejamento das execuções do Apify (porte de <c>app.py:1043-1788</c>).</summary>
public static class LinkedInSearch
{
    public static readonly HashSet<int> ExperienceCodes = [1, 2, 3, 4, 5, 6];
    public const int MaxAgeDefault = 7;
    public const int MinAgeSetting = 1;
    public const int MaxAgeSetting = 30;
    public const int FilterMaxKeywords = 2;
    public const int SearchMaxUrls = 8;
    public const int CustomActorMaxRuns = 6;
    public const int CustomActorMaxLocations = 3;
    private static readonly HashSet<string> RemoteLocationWords = ["remote", "remoto", "worldwide", "anywhere", "global", "home office"];

    private static string S(JsonNode? node) => Text.PyStr(node);
    private static string Cfg(IReadOnlyDictionary<string, string>? cfg, string key) => cfg is not null && cfg.TryGetValue(key, out var v) ? v : "";

    /// <summary>Equivale a <c>urllib.parse.quote_plus</c>: só letras, dígitos e <c>_.-~</c> ficam; espaço vira "+".</summary>
    public static string QuotePlus(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value ?? ""))
        {
            var c = (char)b;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-' or '~') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary><c>terms()</c>: valores separados por vírgula, aparados e em minúsculas.</summary>
    public static List<string> Terms(string? value) =>
        (value ?? "").Split(',').Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).ToList();

    // ------------------------------------------------------------ janelas de publicação

    /// <summary>Janela máxima de publicação (dias), do slider do painel: 1 (24 h) a 30. Inválido volta ao padrão.</summary>
    public static int MaxAgeDays(IReadOnlyDictionary<string, string>? cfg)
    {
        var raw = Cfg(cfg, "linkedin_max_age_days");
        var days = raw.Length == 0 ? MaxAgeDefault : int.TryParse(raw.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : MaxAgeDefault;
        return Math.Max(MinAgeSetting, Math.Min(MaxAgeSetting, days));
    }

    /// <summary>Janelas em dias, sempre da mais recente para a mais antiga: 24 h, depois 7 dias, depois o teto.</summary>
    public static List<int> RecencyPhases(int maxDays)
    {
        maxDays = Math.Max(MinAgeSetting, Math.Min(MaxAgeSetting, maxDays));
        return new[] { 1, Math.Min(7, maxDays), maxDays }.Distinct().OrderBy(d => d).ToList();
    }

    public static string Tpr(int days) => $"r{days * 86400}";

    // ------------------------------------------------------------ países com viés

    public static List<(string Name, string City)> PreferredPlaces(IReadOnlyDictionary<string, string>? cfg) =>
        Geo.ParsePreferred(Cfg(cfg, "linkedin_preferred_countries")).Take(Geo.PreferredCountriesMax).ToList();

    public static List<string> PreferredCountries(IReadOnlyDictionary<string, string>? cfg) => PreferredPlaces(cfg).Select(p => p.Name).ToList();

    private static string MinKey(IEnumerable<string> names, string fallback)
    {
        var list = names.ToList();
        return list.Count == 0 ? fallback : list.Min(StringComparer.Ordinal)!;
    }

    /// <summary>Pares (local, modelo) das buscas, derivados do Perfil; a IA não escolhe local nem modelo. Países com viés primeiro.</summary>
    public static List<ScopeItem> SearchScope(IReadOnlyDictionary<string, string> cfg)
    {
        var enabled = JobAssessment.GeoEnabled(cfg);
        var remote = enabled ? "2" : "";
        var country = JobAssessment.InferCandidateCountry(cfg).Trim();
        var city = Cfg(cfg, "candidate_city").Trim();
        var places = PreferredPlaces(cfg);
        var pairs = new List<(string Location, string FWt)>();
        foreach (var (name, placeCity) in places) // países com viés antes de qualquer outro
        {
            pairs.Add((name, remote));
            if (enabled && placeCity.Length > 0) pairs.Add((placeCity, "")); // a cidade escolhida para este país, logo depois dele
        }
        if (enabled && city.Length > 0 && country.Length > 0
            && places.Any(p => p.City.Length == 0 && Geo.CountryNames(country).Overlaps(Geo.CountryNames(p.Name))))
            pairs.Add((city, "")); // o país de casa tem viés sem cidade própria: a sua cidade vem logo depois
        pairs.Add(("Worldwide", remote));
        if (country.Length > 0 && country.ToLowerInvariant() != "worldwide") pairs.Add((country, remote));
        if (enabled && city.Length > 0) pairs.Add((city, ""));
        var scope = new List<ScopeItem>();
        var seen = new HashSet<(string, string)>();
        foreach (var (location, fWt) in pairs)
        {
            var key = (MinKey(Geo.CountryNames(location), location.ToLowerInvariant()), fWt); // "Brasil" e "Brazil" são a mesma busca
            if (!seen.Add(key)) continue;
            scope.Add(new ScopeItem(location, fWt, (fWt == "2" ? "Remoto — " : "Qualquer modelo — ") + location));
        }
        return scope;
    }

    // ------------------------------------------------------------ URLs de busca

    /// <summary>Todas as combinações palavra-chave × escopo, da janela mais recente (24 h) até <paramref name="maxAgeDays"/>, sem repetir.</summary>
    public static List<string> BuildSearchUrls(JsonObject filter, IReadOnlyList<ScopeItem> scope, int maxAgeDays = MaxAgeDefault)
    {
        var keywords = (filter["keywords"] as JsonArray ?? []).Select(S).Where(k => k.Trim().Length > 0).Take(FilterMaxKeywords).ToList();
        if (keywords.Count == 0 || scope.Count == 0) return [];
        var experience = (filter["experience_levels"] as JsonArray ?? []).Select(x => (int)JobAssessment.Number(x)).Where(ExperienceCodes.Contains).ToList();
        var fE = experience.Count > 0 ? string.Join("%2C", experience) : "";
        var ordered = new List<string>();
        foreach (var days in RecencyPhases(maxAgeDays)) // mais recente primeiro
            foreach (var item in scope) // dentro da janela, o escopo na ordem dada
                foreach (var keyword in keywords)
                {
                    var url = $"https://www.linkedin.com/jobs/search/?keywords={QuotePlus(keyword)}&location={QuotePlus(item.Location)}&position=1&pageNum=0&f_TPR={Tpr(days)}&sortBy=DD";
                    if (fE.Length > 0) url += $"&f_E={fE}";
                    if (item.FWt.Length > 0) url += $"&f_WT={item.FWt}";
                    ordered.Add(url);
                }
        return ordered.Distinct().ToList();
    }

    /// <summary>Principal sozinho: tudo. Principal + Secundário: fatia configurável (mínimo de 1 vaga cada).</summary>
    public static List<int> SplitApifyCount(int baseCount, int filterCount, int mainShare)
    {
        baseCount = Math.Max(1, baseCount);
        if (filterCount < 2) return Enumerable.Repeat(baseCount, filterCount).ToList();
        var share = Math.Max(50, Math.Min(95, mainShare));
        var main = Math.Max(1, (int)(baseCount * share / 100.0 + 0.5));
        return [main, Math.Max(1, baseCount - main)];
    }

    // ------------------------------------------------------------ actors custom

    public static bool CustomActorUsesSearchPlaceholders(JsonObject actor)
    {
        var blob = actor["input_template"] is JsonObject or JsonArray ? actor["input_template"]!.ToJsonString() : "";
        return blob.Contains("{{keyword", StringComparison.Ordinal) || blob.Contains("{{location", StringComparison.Ordinal);
    }

    /// <summary>Locais das buscas custom: os do painel sem repetir sinônimos, sempre com "remote", o país e a cidade (no máximo 3).</summary>
    public static List<string> CustomSearchLocations(IReadOnlyDictionary<string, string> cfg)
    {
        var output = new List<string>();
        var seen = new HashSet<string>();
        void Add(string name)
        {
            name = name.Trim();
            var norm = Text.NormalizeText(name);
            if (norm.Length == 0) return;
            var key = RemoteLocationWords.Contains(norm) ? "remote" : MinKey(Geo.CountryNames(name), norm);
            if (seen.Add(key)) output.Add(name);
        }
        foreach (var name in Terms(Cfg(cfg, "locations"))) Add(name);
        Add("remote");
        Add(JobAssessment.InferCandidateCountry(cfg));
        Add(Cfg(cfg, "candidate_city"));
        return output.Take(CustomActorMaxLocations).ToList();
    }

    private static JsonObject Spread(JsonObject actor, params (string Key, JsonNode? Value)[] changes)
    {
        var copy = (JsonObject)actor.DeepClone();
        foreach (var (key, value) in changes) copy[key] = value;
        return copy;
    }

    /// <summary>Uma execução por (cargo do filtro × local). Primeiro o 1º cargo de cada filtro em todos os locais, depois as variantes, até o teto.</summary>
    public static List<PlannedRun> PlanCustomActorRuns(JsonObject actor, IReadOnlyList<JsonObject> active, IReadOnlyList<string> fallbackKeywords, IReadOnlyList<string> locations)
    {
        var pairs = new List<(JsonObject? Item, string Keyword, string Location)>();
        if (active.Count > 0)
        {
            var deepest = active.Max(item => (item["keywords"] as JsonArray)?.Count ?? 0);
            for (var index = 0; index < deepest; index++)
                foreach (var location in locations)
                    foreach (var item in active)
                    {
                        var keywords = item["keywords"] as JsonArray ?? [];
                        if (index < keywords.Count) pairs.Add((item, S(keywords[index]), location));
                    }
        }
        else pairs = fallbackKeywords.Take(3).SelectMany(keyword => locations.Select(location => ((JsonObject?)null, keyword, location))).ToList();
        var runs = new List<(JsonObject? Item, string Keyword, string Location)>();
        var seen = new HashSet<(string, string)>();
        foreach (var (item, keyword, location) in pairs)
        {
            if (!seen.Add((keyword.ToLowerInvariant(), location.ToLowerInvariant()))) continue;
            runs.Add((item, keyword, location));
            if (runs.Count >= CustomActorMaxRuns) break;
        }
        var count = Text.Truthy(actor["count"]) ? (int)JobAssessment.Number(actor["count"]) : 25;
        var perRun = runs.Count <= 1 ? count : Math.Max(5, (count * 2 + runs.Count - 1) / runs.Count); // ~2x o count do actor no total
        var label = Text.Truthy(actor["label"]) ? S(actor["label"]) : S(actor["id"]);
        var planned = new List<PlannedRun>();
        foreach (var (item, keyword, location) in runs)
        {
            var context = new JsonObject { ["keyword"] = keyword, ["keyword0"] = keyword, ["location"] = location, ["location0"] = location };
            var name = item is not null ? $"{label}: {S(item["name"])} ({location})" : $"{label}: {keyword} ({location})";
            planned.Add(new PlannedRun(Spread(actor, ("count", perRun), ("label", name), ("_ctx", context)), item, null));
        }
        return planned;
    }

    /// <summary>Intercala as execuções de cada actor (uma de cada por vez), mantendo a ordem interna de cada um.</summary>
    public static List<T> Interleave<T>(IReadOnlyList<List<T>> groups)
    {
        var merged = new List<T>();
        for (var position = 0; position < (groups.Count == 0 ? 0 : groups.Max(g => g.Count)); position++)
            foreach (var group in groups)
                if (position < group.Count) merged.Add(group[position]);
        return merged;
    }

    public sealed record UrlPart(List<string> Urls, int Count, bool Remote);

    /// <summary>Separa as URLs do lote em buscas com o filtro remoto do LinkedIn (f_WT=2) e as demais; o <paramref name="count"/> é repartido na proporção.</summary>
    public static List<UrlPart> SplitUrlsByWorkModel(List<string> batch, int count)
    {
        var remote = batch.Where(u => u.Contains("&f_WT=2", StringComparison.Ordinal)).ToList();
        var other = batch.Where(u => !u.Contains("&f_WT=2", StringComparison.Ordinal)).ToList();
        if (remote.Count == 0 || other.Count == 0) return [new UrlPart(batch, count, remote.Count > 0)];
        var first = count > 1 ? Math.Max(1, Math.Min(count - 1, (int)Text.PyRound((double)count * remote.Count / batch.Count, 0))) : 1;
        return [new UrlPart(remote, first, true), new UrlPart(other, Math.Max(1, count - first), false)];
    }

    // ------------------------------------------------------------ planejamento

    /// <summary>Divide a quantidade entre Principal e Secundário e prioriza os lotes do dia; os actors são intercalados.</summary>
    public static List<PlannedRun> PlanApifyRuns(IReadOnlyList<JsonObject> actors, JsonObject? main, JsonObject? secondary, IReadOnlyList<ScopeItem> scope,
        int mainShare = 70, int maxAgeDays = MaxAgeDefault, IReadOnlyList<string>? customLocations = null, IReadOnlyList<string>? fallbackKeywords = null)
    {
        var groups = new List<List<PlannedRun>>();
        var active = new[] { main, secondary }.Where(item => item is not null && (item["enabled"] is null || Text.Truthy(item["enabled"]))).Select(item => item!).ToList();
        foreach (var actor in actors)
        {
            if (actor["enabled"] is not null && !Text.Truthy(actor["enabled"])) continue;
            if (S(actor["input_mode"]) != "linkedin_search")
            {
                if (customLocations is { Count: > 0 } && CustomActorUsesSearchPlaceholders(actor))
                    groups.Add(PlanCustomActorRuns(actor, active, fallbackKeywords ?? [], customLocations));
                else groups.Add([new PlannedRun(actor, null, null)]);
                continue;
            }
            if (active.Count == 0) continue;
            var planned = new List<PlannedRun>();
            var actorCount = Text.Truthy(actor["count"]) ? (int)JobAssessment.Number(actor["count"]) : 25;
            var counts = SplitApifyCount(actorCount, active.Count, mainShare);
            var searches = active.Select((item, i) => (Item: item, Urls: BuildSearchUrls(item, scope, maxAgeDays), Count: counts[i])).ToList();
            foreach (var days in RecencyPhases(maxAgeDays)) // janelas mais recentes sempre primeiro
            {
                var tpr = Tpr(days);
                foreach (var (item, urls, count) in searches)
                {
                    var phaseUrls = urls.Where(url => url.Contains($"f_TPR={tpr}", StringComparison.Ordinal)).ToList();
                    for (var offset = 0; offset < phaseUrls.Count; offset += SearchMaxUrls)
                    {
                        var batch = phaseUrls.Skip(offset).Take(SearchMaxUrls).ToList();
                        var label = Text.Truthy(actor["label"]) ? S(actor["label"]) : S(actor["id"]);
                        var phase = days == 1 ? "24h" : $"{days}d";
                        var parts = SplitUrlsByWorkModel(batch, count);
                        foreach (var part in parts)
                        {
                            var suffix = parts.Count > 1 ? (part.Remote ? ", remoto" : ", qualquer modelo") : "";
                            var runActor = Spread(actor, ("count", part.Count), ("_search_remote", part.Remote),
                                ("label", $"{label}: {S(item["name"])} ({phase}, lote {offset / SearchMaxUrls + 1}{suffix})"));
                            planned.Add(new PlannedRun(runActor, item, part.Urls));
                        }
                    }
                }
            }
            groups.Add(planned);
        }
        return Interleave(groups);
    }
}
