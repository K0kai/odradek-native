using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Odradek.Core.Candidate;

/// <summary>Texto normalizado pronto para checagens repetidas de citação (<c>NormalizedText</c> do Python).</summary>
public sealed class NormalizedText
{
    public string Text { get; }
    public string[] Tokens { get; }
    public Dictionary<string, List<int>> Positions { get; }

    private NormalizedText(string text, string[] tokens, Dictionary<string, List<int>> positions)
    {
        Text = text;
        Tokens = tokens;
        Positions = positions;
    }

    public static NormalizedText Of(string? raw)
    {
        var norm = Core.Text.NormalizeText(raw);
        var tokens = norm.Length == 0 ? [] : norm.Split(' ');
        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!positions.TryGetValue(tokens[i], out var list)) positions[tokens[i]] = list = [];
            list.Add(i);
        }
        return new NormalizedText($" {norm} ", tokens, positions);
    }
}

/// <summary>
/// Perfil canônico do currículo (porte de <c>candidate/resume_profile.py</c>): fatos com citação verificada pelo código.
/// O perfil é um <see cref="JsonObject"/> com as mesmas chaves, na mesma ordem, do JSON que o app Python grava em <c>profile_json</c>.
/// </summary>
public static partial class ResumeProfile
{
    public const string Version = "2";
    private static readonly string[] SkillCategories = ["language", "framework", "tool", "cloud", "db", "domain", "soft", "other"];

    // ------------------------------------------------------------ similaridade (difflib.SequenceMatcher)

    /// <summary>Soma dos blocos casados de <c>SequenceMatcher(None, a, b, autojunk=False)</c>: o <c>ratio()</c> é 2·M/(|a|+|b|).</summary>
    internal static int MatchingTotal(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var b2j = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var j = 0; j < b.Count; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list)) b2j[b[j]] = list = [];
            list.Add(j);
        }
        var total = 0;
        var stack = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        stack.Push((0, a.Count, 0, b.Count));
        while (stack.Count > 0)
        {
            var (alo, ahi, blo, bhi) = stack.Pop();
            var (i, j, k) = LongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (k == 0) continue;
            total += k;
            if (alo < i && blo < j) stack.Push((alo, i, blo, j));
            if (i + k < ahi && j + k < bhi) stack.Push((i + k, ahi, j + k, bhi));
        }
        return total;
    }

    private static (int I, int J, int K) LongestMatch(IReadOnlyList<string> a, IReadOnlyList<string> b, Dictionary<string, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newJ2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var positions))
            {
                foreach (var j in positions)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    var k = (j2len.TryGetValue(j - 1, out var previous) ? previous : 0) + 1;
                    newJ2len[j] = k;
                    if (k > bestsize) { besti = i - k + 1; bestj = j - k + 1; bestsize = k; }
                }
            }
            j2len = newJ2len;
        }
        return (besti, bestj, bestsize);
    }

    /// <summary>Citação existe no texto: substring normalizada ou aproximação por janela de tokens (limiar 0,9).</summary>
    public static bool QuoteInText(string? quote, NormalizedText source, double threshold = 0.9)
    {
        var norm = Core.Text.NormalizeText(quote);
        if (norm.Length < 3) return false;
        if (source.Text.Contains($" {norm} ", StringComparison.Ordinal)) return true;
        var q = norm.Split(' ');
        if (q.Length < 3) return false;
        var n = q.Length;
        var starts = new SortedSet<int>();
        foreach (var token in q.Take(3))
            if (source.Positions.TryGetValue(token, out var positions))
                foreach (var pos in positions)
                    for (var s = Math.Max(0, pos - 2); s <= pos; s++) starts.Add(s);
        var best = 0.0;
        foreach (var start in starts)
        {
            var length = Math.Max(0, Math.Min(n + 3, source.Tokens.Length - start));
            var window = new ArraySegment<string>(source.Tokens, start, length);
            var total = q.Length + window.Count;
            if (2.0 * Math.Min(q.Length, window.Count) / total < threshold * 0.8) continue; // real_quick_ratio
            best = Math.Max(best, 2.0 * MatchingTotal(q, window) / total);
            if (best >= threshold) return true;
        }
        return false;
    }

    public static bool TermInText(string? term, NormalizedText source)
    {
        var norm = Core.Text.NormalizeText(term);
        return norm.Length > 0 && source.Text.Contains($" {norm} ", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ seções

    private static readonly Dictionary<string, string[]> SectionKinds = new()
    {
        ["experience"] = ["experiencia", "experiencia profissional", "experiencia de trabalho", "historico profissional", "work experience", "professional experience", "experience", "employment history", "work history", "career history", "atuacao profissional"],
        ["education"] = ["formacao", "formacao academica", "educacao", "escolaridade", "education", "academic background", "cursos", "courses"],
        ["skills"] = ["habilidades", "competencias", "skills", "technical skills", "competencias tecnicas", "tecnologias", "conhecimentos", "hard skills", "soft skills", "ferramentas", "tools"],
        ["projects"] = ["projetos", "projects", "projetos relevantes", "portfolio"],
        ["certifications"] = ["certificacoes", "certificados", "certifications", "licencas", "licenses"],
        ["languages"] = ["idiomas", "languages", "linguas"],
        ["summary"] = ["resumo", "resumo profissional", "summary", "professional summary", "perfil", "perfil profissional", "profile", "objetivo", "objective", "sobre mim", "about", "about me"],
        ["contact"] = ["contato", "contact", "dados pessoais", "personal information"],
        ["achievements"] = ["conquistas", "achievements", "realizacoes", "premios", "awards"],
        ["other"] = ["informacoes adicionais", "additional information", "voluntariado", "volunteering", "publicacoes", "publications", "interesses", "interests", "referencias", "references"],
    };

    private static readonly Dictionary<string, string> HeadingLookup =
        SectionKinds.SelectMany(kv => kv.Value.Select(name => (name, kind: kv.Key))).GroupBy(x => x.name).ToDictionary(g => g.Key, g => g.Last().kind);

    public sealed record Section(string Title, string Kind, int Start, int End, string Text);

    private static string? HeadingKind(string line)
    {
        var stripped = line.Trim().Trim(':', '-', '–', '—', '•', '*', '#', '|').Trim();
        if (stripped.Length == 0 || stripped.Length > 45 || PyWords(stripped).Count > 5) return null;
        if (".,;".Contains(stripped[^1])) return null;
        return HeadingLookup.TryGetValue(Core.Text.NormalizeText(stripped), out var kind) ? kind : null;
    }

    private static List<string> PyWords(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Divide o texto em seções por cabeçalhos conhecidos (PT/EN). O que vem antes do primeiro vira "header".</summary>
    public static List<Section> SplitSections(string text)
    {
        var sections = new List<Section>();
        string currentTitle = "", currentKind = "header";
        var currentStart = 0;
        var cursor = 0;
        foreach (var line in SplitKeepEnds(text))
        {
            var kind = HeadingKind(line);
            if (kind is not null)
            {
                var body = text[currentStart..cursor];
                if (body.Trim().Length > 0) sections.Add(new Section(currentTitle, currentKind, currentStart, cursor, body));
                currentTitle = line.Trim().Trim(':');
                currentKind = kind;
                currentStart = cursor;
            }
            cursor += line.Length;
        }
        var tail = text[currentStart..];
        if (tail.Trim().Length > 0) sections.Add(new Section(currentTitle, currentKind, currentStart, text.Length, tail));
        return sections;
    }

    /// <summary><c>str.splitlines(keepends=True)</c>.</summary>
    private static IEnumerable<string> SplitKeepEnds(string value)
    {
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\x2028' or '\x2029')) continue;
            if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n') i++;
            yield return value[start..(i + 1)];
            start = i + 1;
        }
        if (start < value.Length) yield return value[start..];
    }

    // ------------------------------------------------------------ datas e derivados

    [GeneratedRegex(@"^(\d{4})-(\d{2})$")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex YearRegex();

    private static string CleanDate(JsonNode? value, bool allowPresent)
    {
        var raw = Core.Text.PyStr(value).Trim().ToLowerInvariant();
        if (allowPresent && raw is "present" or "atual" or "current" or "now" or "hoje") return "present";
        var match = DateRegex().Match(raw);
        if (match.Success && int.Parse(match.Groups[2].Value) is >= 1 and <= 12 && int.Parse(match.Groups[1].Value) is >= 1950 and <= 2100) return raw;
        if (YearRegex().IsMatch(raw) && int.Parse(raw) is >= 1950 and <= 2100) return allowPresent ? $"{raw}-12" : $"{raw}-01";
        return "";
    }

    private static int? MonthIndex(string value, DateOnly today)
    {
        if (value == "present") return today.Year * 12 + today.Month - 1;
        var match = DateRegex().Match(value);
        return match.Success ? int.Parse(match.Groups[1].Value) * 12 + int.Parse(match.Groups[2].Value) - 1 : null;
    }

    private static (int Start, int End)? Interval(JsonObject role, DateOnly today)
    {
        var start = MonthIndex(S(role["start"]), today);
        var end = MonthIndex(S(role["end"]), today);
        if (start is null || end is null || end < start) return null;
        return (start.Value, end.Value + 1); // fim exclusivo: jan..jan = 1 mês
    }

    public static int UnionMonths(IEnumerable<(int Start, int End)> intervals)
    {
        var total = 0;
        int? lastEnd = null;
        foreach (var (start, end) in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (lastEnd is null || start > lastEnd) { total += end - start; lastEnd = end; }
            else if (end > lastEnd) { total += end - lastEnd.Value; lastEnd = end; }
        }
        return total;
    }

    public static int MonthsForRoles(JsonObject profile, IEnumerable<string> roleIds, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);
        var wanted = roleIds.ToHashSet();
        return UnionMonths(Arr(profile["roles"]).OfType<JsonObject>().Where(r => wanted.Contains(S(r["id"]))).Select(r => Interval(r, day)).Where(i => i is not null).Select(i => i!.Value));
    }

    /// <summary>Meses de formação com período conhecido (união dos cursos; um curso em andamento conta até hoje).</summary>
    public static int EducationMonths(JsonObject profile, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);
        return UnionMonths(Arr(profile["education"]).OfType<JsonObject>().Select(e => Interval(e, day)).Where(i => i is not null).Select(i => i!.Value));
    }

    /// <summary>Há papel sem datas (ou com datas ilegíveis): a experiência medida pode estar subestimada.</summary>
    public static bool HasUndatedRoles(JsonObject profile, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);
        return Arr(profile["roles"]).OfType<JsonObject>().Any(r => Interval(r, day) is null);
    }

    private static string IndexToYm(int index) => $"{index / 12:0000}-{index % 12 + 1:00}";

    private static string Compact(string? value) => Core.Text.NormalizeText(value).Replace(" ", "");

    /// <summary>Nulo e valores "falsos" viram "" (<c>str(x or "")</c>).</summary>
    private static string S(JsonNode? node) => Core.Text.PyStr(node);
    private static IEnumerable<JsonNode?> Arr(JsonNode? node) => node is JsonArray array ? array : [];

    public static JsonObject ComputeDerived(JsonObject profile, DateOnly? today = null)
    {
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);
        var roles = Arr(profile["roles"]).OfType<JsonObject>().ToList();
        var intervals = new Dictionary<string, (int Start, int End)>();
        foreach (var role in roles)
            if (Interval(role, day) is { } iv) intervals[S(role["id"])] = iv;
        var roleNorm = new Dictionary<string, NormalizedText>();
        foreach (var role in roles)
        {
            var blob = string.Join(" ", new[] { string.Join(" ", Arr(role["skills_used"]).Select(S)) }
                .Concat(Arr(role["responsibilities"]).OfType<JsonObject>().Select(r => $"{S(r["text"])} {S(r["quote"])}")));
            roleNorm[S(role["id"])] = NormalizedText.Of(blob);
        }
        var projectIntervals = new Dictionary<string, (int Start, int End)>();
        foreach (var project in Arr(profile["projects"]).OfType<JsonObject>())
            if (Core.Text.Truthy(project["start"]) && Interval(project, day) is { } iv) projectIntervals[S(project["id"])] = iv;
        var projectStacks = Arr(profile["projects"]).OfType<JsonObject>().Where(p => projectIntervals.ContainsKey(S(p["id"])))
            .ToDictionary(p => S(p["id"]), p => Arr(p["stack"]).Select(s => Compact(S(s))).ToHashSet());

        var skills = new JsonObject();
        foreach (var skill in Arr(profile["skills"]).OfType<JsonObject>())
        {
            var canonical = Core.Text.Truthy(skill["canonical"]) ? S(skill["canonical"]) : Core.Text.NormalizeText(S(skill["name"]));
            var used = roles.Where(role => intervals.ContainsKey(S(role["id"])) && (
                    Arr(role["skills_used"]).Select(s => Core.Text.NormalizeText(S(s))).Contains(canonical)
                    || TermInText(canonical, roleNorm[S(role["id"])])
                    || TermInText(S(skill["name"]), roleNorm[S(role["id"])])))
                .Select(role => S(role["id"])).ToList();
            var months = UnionMonths(used.Select(i => intervals[i]));
            int? last = used.Count == 0 ? null : used.Max(i => intervals[i].End - 1);
            var keys = new HashSet<string> { Compact(canonical), Compact(S(skill["name"])) };
            keys.Remove("");
            var projectUsed = projectStacks.Where(kv => keys.Overlaps(kv.Value)).Select(kv => kv.Key).ToList();
            int? projectLast = projectUsed.Count == 0 ? null : projectUsed.Max(i => projectIntervals[i].End - 1);
            skills[S(skill["id"])] = new JsonObject
            {
                ["canonical"] = canonical,
                ["months"] = months,
                ["last_used"] = last is { } l ? IndexToYm(l) : "",
                ["role_ids"] = new JsonArray(used.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["project_months"] = UnionMonths(projectUsed.Select(i => projectIntervals[i])),
                ["project_ids"] = new JsonArray(projectUsed.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["project_last_used"] = projectLast is { } pl ? IndexToYm(pl) : "",
            };
        }
        var roleMonths = new JsonObject();
        foreach (var (id, (start, end)) in intervals) roleMonths[id] = end - start;
        return new JsonObject
        {
            ["total_months"] = UnionMonths(intervals.Values),
            ["skills"] = skills,
            ["role_months"] = roleMonths,
            ["current_role_ids"] = new JsonArray(roles.Where(r => S(r["end"]) == "present").Select(r => (JsonNode?)JsonValue.Create(S(r["id"]))).ToArray()),
        };
    }

    // ------------------------------------------------------------ ancoragem

    private static string Str(JsonNode? value, int limit = 600) => Core.Text.Take(string.Join(" ", S(value).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), limit);

    private static JsonArray ToArray(IEnumerable<JsonNode?> items) => new(items.ToArray());

    /// <summary>Valida o perfil extraído pela IA contra o texto do PDF e atribui IDs estáveis; fatos sem citação encontrada são descartados e contados.</summary>
    public static JsonObject GroundProfile(JsonObject raw, string sourceText, DateOnly? today = null)
    {
        var source = NormalizedText.Of(sourceText);
        var dropped = new Dictionary<string, int>();
        var kept = 0;
        bool Grounded(JsonNode? quote) => quote is JsonValue v && v.TryGetValue<string>(out var text) && QuoteInText(text, source);
        void Drop(string kind) => dropped[kind] = dropped.GetValueOrDefault(kind) + 1;

        var roles = new JsonArray();
        var skills = new JsonArray();
        var transferable = new JsonArray();
        var named = new Dictionary<string, JsonArray> { ["education"] = [], ["certifications"] = [], ["projects"] = [], ["achievements"] = [] };
        var languages = new JsonArray();
        var profile = new JsonObject
        {
            ["version"] = Version,
            ["headline"] = Str(raw["headline"], 400),
            ["professional_profile"] = Str(raw["professional_profile"], 4000),
            ["seniority"] = Str(raw["seniority"], 120),
            ["location_notes"] = Str(raw["location_notes"], 600),
            ["work_authorization_notes"] = Str(raw["work_authorization_notes"], 600),
            ["roles"] = roles, ["skills"] = skills, ["transferable_skills"] = transferable,
            ["education"] = named["education"], ["certifications"] = named["certifications"], ["projects"] = named["projects"],
            ["achievements"] = named["achievements"], ["languages"] = languages,
        };

        var roleIdsByTitle = new Dictionary<string, string>();
        foreach (var role in Arr(raw["roles"]).OfType<JsonObject>())
        {
            if (Str(role["title"]).Length == 0 && Str(role["employer"]).Length == 0) continue;
            if (!Grounded(role["quote"]) && !Grounded(JsonValue.Create($"{S(role["title"])} {S(role["employer"])}"))) { Drop("roles"); continue; }
            var rid = $"R{roles.Count + 1}";
            var responsibilities = new JsonArray();
            foreach (var item in Arr(role["responsibilities"]))
            {
                if (item is JsonObject obj && Str(obj["text"]).Length > 0 && Grounded(obj["quote"]))
                    responsibilities.Add(new JsonObject { ["id"] = $"{rid}.{responsibilities.Count + 1}", ["text"] = Str(obj["text"], 400), ["quote"] = Str(obj["quote"], 400) });
                else Drop("responsibilities");
            }
            var entry = new JsonObject
            {
                ["id"] = rid, ["title"] = Str(role["title"], 160), ["employer"] = Str(role["employer"], 160),
                ["start"] = CleanDate(role["start"], false), ["end"] = CleanDate(role["end"], true),
                ["quote"] = Str(role["quote"], 400), ["responsibilities"] = responsibilities,
                ["skills_used"] = ToArray(Arr(role["skills_used"]).Where(s => Str(s).Length > 0).Select(s => (JsonNode?)JsonValue.Create(Str(s, 80))).Take(40)),
            };
            roles.Add(entry);
            roleIdsByTitle[Core.Text.NormalizeText(S(entry["title"]))] = rid;
            kept++;
        }

        foreach (var skill in Arr(raw["skills"]).OfType<JsonObject>())
        {
            if (Str(skill["name"]).Length == 0) continue;
            var quotes = Arr(skill["quotes"]).Where(Grounded).Select(q => Str(q, 300)).ToList();
            if (quotes.Count == 0 && !TermInText(S(skill["name"]), source)) { Drop("skills"); continue; }
            var category = SkillCategories.Contains(S(skill["category"])) ? S(skill["category"]) : "other";
            var canonical = Core.Text.NormalizeText(Core.Text.Truthy(skill["canonical"]) ? S(skill["canonical"]) : S(skill["name"]));
            if (skills.OfType<JsonObject>().Any(s => S(s["canonical"]) == canonical)) continue;
            skills.Add(new JsonObject
            {
                ["id"] = $"S{skills.Count + 1}", ["name"] = Str(skill["name"], 80), ["canonical"] = canonical, ["category"] = category,
                ["quotes"] = ToArray(quotes.Take(3).Select(q => (JsonNode?)JsonValue.Create(q))),
            });
            kept++;
        }

        foreach (var item in Arr(raw["transferable_skills"]).OfType<JsonObject>())
        {
            if (Str(item["capability"]).Length == 0) continue;
            var quotes = Arr(item["quotes"]).Where(Grounded).Select(q => Str(q, 300)).ToList();
            if (quotes.Count == 0) { Drop("transferable_skills"); continue; }
            var fromRoles = Arr(item["from_roles"]).Select(t => Core.Text.NormalizeText(Core.Text.PyStrRaw(t))).Where(roleIdsByTitle.ContainsKey).Select(k => (JsonNode?)JsonValue.Create(roleIdsByTitle[k]));
            transferable.Add(new JsonObject
            {
                ["id"] = $"T{transferable.Count + 1}", ["capability"] = Str(item["capability"], 200),
                ["from_role_ids"] = ToArray(fromRoles), ["quotes"] = ToArray(quotes.Take(3).Select(q => (JsonNode?)JsonValue.Create(q))),
            });
            kept++;
        }

        foreach (var (key, prefix) in new[] { ("education", "E"), ("certifications", "C"), ("projects", "P"), ("achievements", "A") })
        {
            foreach (var item in Arr(raw[key]))
            {
                if (item is JsonObject obj && Str(obj["text"]).Length > 0 && Grounded(obj["quote"]))
                {
                    var fact = new JsonObject { ["id"] = $"{prefix}{named[key].Count + 1}", ["text"] = Str(obj["text"], 400), ["quote"] = Str(obj["quote"], 400) };
                    if (key == "education" && CleanDate(obj["start"], false) is { Length: > 0 } eduStart && CleanDate(obj["end"], true) is { Length: > 0 } eduEnd)
                    {
                        fact["start"] = eduStart; // só quando o período é conhecido: o perfil de quem não tem datas continua igual
                        fact["end"] = eduEnd;
                    }
                    named[key].Add(fact);
                    kept++;
                }
                else Drop(key);
            }
        }

        foreach (var item in Arr(raw["languages"]))
        {
            if (item is JsonObject obj && Str(obj["language"]).Length > 0 && Grounded(obj["quote"]))
            {
                languages.Add(new JsonObject
                {
                    ["id"] = $"L{languages.Count + 1}", ["language"] = Str(obj["language"], 60), ["level"] = Str(obj["level"], 60), ["quote"] = Str(obj["quote"], 300),
                });
                kept++;
            }
            else Drop("languages");
        }

        profile["derived"] = ComputeDerived(profile, today);
        var droppedNode = new JsonObject();
        foreach (var (k, v) in dropped) droppedNode[k] = v;
        profile["coverage"] = new JsonObject { ["kept"] = kept, ["dropped"] = droppedNode, ["ungrounded"] = dropped.Values.Sum() };
        return profile;
    }

    // ------------------------------------------------------------ fusão PT/EN e renderizações

    private static string RoleKey(JsonObject role) =>
        $"{(Core.Text.NormalizeText(S(role["employer"])) is { Length: > 0 } e ? e : Core.Text.NormalizeText(S(role["title"])))}|{S(role["start"])}";

    private static string ItemMarker(JsonObject item)
    {
        foreach (var key in new[] { "canonical", "text", "language" })
            if (Core.Text.Truthy(item[key])) return Compact(S(item[key]));
        return Compact(S(item["capability"]));
    }

    private static JsonObject? FindRole(JsonArray roles, JsonObject role)
    {
        foreach (var existing in roles.OfType<JsonObject>())
            if (RoleKey(existing) == RoleKey(role)) return existing;
        var start = S(role["start"]);
        var employer = Core.Text.NormalizeText(S(role["employer"]));
        var title = Core.Text.NormalizeText(S(role["title"]));
        if (start.Length == 0) return null;
        foreach (var existing in roles.OfType<JsonObject>())
        {
            if (S(existing["start"]) != start) continue;
            var other = Core.Text.NormalizeText(S(existing["employer"]));
            if (employer.Length > 0 && other.Length > 0 && (other.Contains(employer, StringComparison.Ordinal) || employer.Contains(other, StringComparison.Ordinal))) return existing;
            if (title.Length > 0 && title == Core.Text.NormalizeText(S(existing["title"]))) return existing;
        }
        return null;
    }

    private static bool EnrichProject(JsonArray projects, JsonObject incoming)
    {
        var title = Core.Text.NormalizeText(S(incoming["title"]));
        if (title.Length < 4) return false;
        foreach (var existing in projects.OfType<JsonObject>())
        {
            if (S(existing["title"]) == S(incoming["title"]) && Core.Text.Truthy(existing["start"])) return true; // já fundido
            if ($" {Core.Text.NormalizeText(S(existing["text"]))} ".Contains($" {title} ", StringComparison.Ordinal))
            {
                foreach (var key in new[] { "title", "slug", "headline", "start", "end", "stack", "source", "url" })
                    if (Core.Text.Truthy(incoming[key])) existing[key] = incoming[key]!.DeepClone();
                return true;
            }
        }
        return false;
    }

    /// <summary>Une perfis (PT/EN) sem duplicar: o primeiro da lista é o preferencial.</summary>
    public static JsonObject? MergeProfiles(IEnumerable<JsonObject> input, DateOnly? today = null)
    {
        var profiles = input.Where(p => p["roles"] is not null).ToList();
        if (profiles.Count == 0) return null;
        if (profiles.Count == 1) return profiles[0];
        var baseProfile = (JsonObject)profiles[0].DeepClone();
        var baseRoles = (JsonArray)baseProfile["roles"]!;
        foreach (var other in profiles.Skip(1))
        {
            foreach (var role in Arr(other["roles"]).OfType<JsonObject>())
            {
                var existing = FindRole(baseRoles, role);
                if (existing is null) { baseRoles.Add(role.DeepClone()); continue; }
                var responsibilities = (JsonArray)existing["responsibilities"]!;
                var known = responsibilities.OfType<JsonObject>().Select(r => Core.Text.NormalizeText(S(r["text"]))).ToHashSet();
                foreach (var resp in Arr(role["responsibilities"]).OfType<JsonObject>())
                    if (!known.Contains(Core.Text.NormalizeText(S(resp["text"])))) responsibilities.Add(resp.DeepClone());
                var used = (JsonArray)existing["skills_used"]!;
                foreach (var skill in Arr(role["skills_used"]))
                    if (!used.Select(s => Core.Text.NormalizeText(S(s))).Contains(Core.Text.NormalizeText(S(skill)))) used.Add(skill?.DeepClone());
            }
            foreach (var key in new[] { "skills", "education", "certifications", "projects", "achievements", "languages", "transferable_skills" })
            {
                var items = (JsonArray)baseProfile[key]!;
                var knownItems = items.OfType<JsonObject>().Select(ItemMarker).ToHashSet();
                foreach (var item in Arr(other[key]).OfType<JsonObject>())
                {
                    var marker = ItemMarker(item);
                    if (key == "projects" && EnrichProject(items, item)) continue;
                    if (marker.Length > 0 && knownItems.Add(marker)) items.Add(item.DeepClone());
                }
            }
            foreach (var key in new[] { "headline", "professional_profile", "seniority", "location_notes", "work_authorization_notes" })
                baseProfile[key] = Core.Text.Truthy(baseProfile[key]) ? baseProfile[key] : other[key]?.DeepClone() ?? "";
        }
        // IDs estáveis reatribuídos sobre o conjunto fundido
        var index = 0;
        foreach (var role in baseRoles.OfType<JsonObject>())
        {
            index++;
            role["id"] = $"R{index}";
            var sub = 0;
            foreach (var resp in ((JsonArray)role["responsibilities"]!).OfType<JsonObject>()) resp["id"] = $"R{index}.{++sub}";
        }
        foreach (var (key, prefix) in new[] { ("skills", "S"), ("transferable_skills", "T"), ("education", "E"), ("certifications", "C"), ("projects", "P"), ("achievements", "A"), ("languages", "L") })
        {
            var n = 0;
            foreach (var item in ((JsonArray)baseProfile[key]!).OfType<JsonObject>()) item["id"] = $"{prefix}{++n}";
        }
        foreach (var item in ((JsonArray)baseProfile["transferable_skills"]!).OfType<JsonObject>()) item["from_role_ids"] = new JsonArray(); // IDs de origem não sobrevivem à fusão
        baseProfile["derived"] = ComputeDerived(baseProfile, today);
        long Coverage(JsonObject p, string field) => p["coverage"] is JsonObject c && c[field] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        baseProfile["coverage"] = new JsonObject
        {
            ["kept"] = profiles.Sum(p => Coverage(p, "kept")), ["dropped"] = new JsonObject(), ["ungrounded"] = profiles.Sum(p => Coverage(p, "ungrounded")),
        };
        return baseProfile;
    }

    public static string FormatMonths(long months)
    {
        var m = Math.Max(0, months);
        var (years, rest) = (m / 12, m % 12);
        if (years > 0 && rest > 0) return $"{years}y{rest}m";
        return years > 0 ? $"{years}y" : $"{rest}m";
    }

    private static long Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : 0;

    /// <summary>Resumo com IDs para a triagem. Ordem: papéis → skills → transferíveis → resto.</summary>
    public static string CompactProfileText(JsonObject profile, int maxChars = 6000)
    {
        var derived = profile["derived"] as JsonObject ?? [];
        var skillStats = derived["skills"] as JsonObject ?? [];
        var lines = new List<string> { $"TOTAL PROFESSIONAL EXPERIENCE (computed from dated roles): {FormatMonths(Num(derived["total_months"]))}" };
        if (Core.Text.Truthy(profile["seniority"])) lines.Add($"SELF-DESCRIBED LEVEL: {S(profile["seniority"])}");
        // Idiomas, local e autorização são curtos e decisivos: ficam no topo, longe do corte por max_chars.
        if (Core.Text.Truthy(profile["languages"]))
            lines.Add("LANGUAGES: " + string.Join("; ", Arr(profile["languages"]).OfType<JsonObject>().Select(i => $"[{S(i["id"])}] {S(i["language"])} {S(i["level"])}".Trim())));
        if (Core.Text.Truthy(profile["location_notes"])) lines.Add($"LOCATION: {S(profile["location_notes"])}");
        if (Core.Text.Truthy(profile["work_authorization_notes"])) lines.Add($"WORK AUTHORIZATION: {S(profile["work_authorization_notes"])}");
        lines.Add("ROLES");
        foreach (var role in Arr(profile["roles"]).OfType<JsonObject>())
        {
            var months = Num((derived["role_months"] as JsonObject)?[S(role["id"])]);
            var span = $"{(Core.Text.Truthy(role["start"]) ? S(role["start"]) : "?")} → {(Core.Text.Truthy(role["end"]) ? S(role["end"]) : "?")}" + (months > 0 ? $", {FormatMonths(months)}" : "");
            lines.Add($"[{S(role["id"])}] {S(role["title"])} — {S(role["employer"])} ({span})");
            foreach (var resp in Arr(role["responsibilities"]).OfType<JsonObject>().Take(8))
                lines.Add($"  [{S(resp["id"])}] {Core.Text.Take(S(resp["text"]), 220)}");
        }
        lines.Add("SKILLS (id name: months used, last used)");
        var undated = new List<string>();
        foreach (var skill in Arr(profile["skills"]).OfType<JsonObject>())
        {
            var stat = skillStats[S(skill["id"])] as JsonObject ?? [];
            var months = Num(stat["months"]);
            var projectMonths = Num(stat["project_months"]);
            if (months == 0 && projectMonths == 0) { undated.Add($"[{S(skill["id"])}] {S(skill["name"])}"); continue; } // sem datas: uma linha só, para não gastar o prompt
            var usage = months > 0 ? $"{FormatMonths(months)}, last {S(stat["last_used"])}" : "no dated professional usage";
            if (projectMonths > 0)
                usage += $"; portfolio projects {FormatMonths(projectMonths)} ({string.Join(",", Arr(stat["project_ids"]).Select(S))}), last {S(stat["project_last_used"])}";
            lines.Add($"[{S(skill["id"])}] {S(skill["name"])}: {usage}");
        }
        if (undated.Count > 0) lines.Add("LISTED SKILLS, NO DATED USAGE: " + string.Join(", ", undated));
        if (Core.Text.Truthy(profile["transferable_skills"]))
        {
            lines.Add("TRANSFERABLE CAPABILITIES");
            foreach (var item in Arr(profile["transferable_skills"]).OfType<JsonObject>()) lines.Add($"[{S(item["id"])}] {S(item["capability"])}");
        }
        foreach (var (key, label) in new[] { ("education", "EDUCATION"), ("certifications", "CERTIFICATIONS"), ("projects", "PROJECTS"), ("achievements", "ACHIEVEMENTS") })
        {
            if (!Core.Text.Truthy(profile[key])) continue;
            lines.Add(label);
            foreach (var item in Arr(profile[key]).OfType<JsonObject>())
            {
                if (key == "projects" && Core.Text.Truthy(item["start"]))
                {
                    var span = $"{S(item["start"])} → {(Core.Text.Truthy(item["end"]) ? S(item["end"]) : "?")}";
                    var stack = string.Join(", ", Arr(item["stack"]).Take(8).Select(S));
                    var headline = Core.Text.Truthy(item["headline"]) ? S(item["headline"]) : S(item["text"]);
                    lines.Add($"[{S(item["id"])}] {(Core.Text.Truthy(item["title"]) ? S(item["title"]) : "Project")} ({span}; {stack}) — {Core.Text.Take(headline, 200)}");
                }
                else if (key == "education" && Core.Text.Truthy(item["start"]) && Core.Text.Truthy(item["end"]))
                    lines.Add($"[{S(item["id"])}] {Core.Text.Take(S(item["text"]), 200)} ({S(item["start"])} → {S(item["end"])})");
                else lines.Add($"[{S(item["id"])}] {Core.Text.Take(S(item["text"]), 200)}");
            }
        }
        return Core.Text.Take(string.Join("\n", lines), maxChars);
    }

    public sealed record Evidence(string Kind, string Text, IReadOnlyList<string> Quotes);

    /// <summary>id → (tipo, texto, citações) para verificar os <c>evidence_ids</c> citados pela triagem.</summary>
    public static Dictionary<string, Evidence> EvidenceIndex(JsonObject profile)
    {
        var index = new Dictionary<string, Evidence>();
        foreach (var role in Arr(profile["roles"]).OfType<JsonObject>())
        {
            index[S(role["id"])] = new Evidence("role", $"{S(role["title"])} {S(role["employer"])}", [S(role["quote"])]);
            foreach (var resp in Arr(role["responsibilities"]).OfType<JsonObject>())
                index[S(resp["id"])] = new Evidence("responsibility", S(resp["text"]), [S(resp["quote"])]);
        }
        foreach (var skill in Arr(profile["skills"]).OfType<JsonObject>())
            index[S(skill["id"])] = new Evidence("skill", S(skill["name"]), Arr(skill["quotes"]).Select(S).ToList());
        foreach (var item in Arr(profile["transferable_skills"]).OfType<JsonObject>())
            index[S(item["id"])] = new Evidence("transferable", S(item["capability"]), Arr(item["quotes"]).Select(S).ToList());
        foreach (var key in new[] { "education", "certifications", "projects", "achievements" })
            foreach (var item in Arr(profile[key]).OfType<JsonObject>())
                index[S(item["id"])] = new Evidence(key, S(item["text"]), [S(item["quote"])]);
        foreach (var item in Arr(profile["languages"]).OfType<JsonObject>())
            index[S(item["id"])] = new Evidence("language", $"{S(item["language"])} {S(item["level"])}", [S(item["quote"])]);
        return index;
    }

    public static JsonObject? ParseProfile(string? raw)
    {
        try { return JsonNode.Parse(raw ?? "") is JsonObject o && S(o["version"]) == Version ? o : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
