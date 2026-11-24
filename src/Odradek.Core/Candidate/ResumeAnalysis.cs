using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Odradek.Core.Ai;

namespace Odradek.Core.Candidate;

/// <summary>Análise do currículo pela IA (porte de <c>candidate/resume_pipeline.py</c> e dos trechos de <c>resume_profile.py</c> que a alimentam).</summary>
public static partial class ResumeAnalysis
{
    // ------------------------------------------------------------ schema e prompt

    private static readonly string[] SkillCategories = ["language", "framework", "tool", "cloud", "db", "domain", "soft", "other"];

    private static JsonObject Type(string type) => new() { ["type"] = type };
    private static JsonObject ListOf(JsonNode items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject ObjectOf(params (string Key, JsonNode Value)[] properties)
    {
        var props = new JsonObject();
        foreach (var (key, value) in properties) props[key] = value;
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    private static JsonObject Fact() => ObjectOf(("text", Type("string")), ("quote", Type("string")));

    public static JsonObject ProfileSchema()
    {
        var category = Type("string");
        category["enum"] = new JsonArray(SkillCategories.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        return ObjectOf(
            ("headline", Type("string")), ("professional_profile", Type("string")), ("seniority", Type("string")),
            ("roles", ListOf(ObjectOf(
                ("title", Type("string")), ("employer", Type("string")), ("start", Type("string")), ("end", Type("string")), ("quote", Type("string")),
                ("responsibilities", ListOf(Fact())), ("skills_used", ListOf(Type("string")))))),
            ("skills", ListOf(ObjectOf(("name", Type("string")), ("canonical", Type("string")), ("category", category), ("quotes", ListOf(Type("string")))))),
            ("transferable_skills", ListOf(ObjectOf(("capability", Type("string")), ("from_roles", ListOf(Type("string"))), ("quotes", ListOf(Type("string")))))),
            ("education", ListOf(Fact())), ("certifications", ListOf(Fact())), ("projects", ListOf(Fact())), ("achievements", ListOf(Fact())),
            ("languages", ListOf(ObjectOf(("language", Type("string")), ("level", Type("string")), ("quote", Type("string"))))),
            ("location_notes", Type("string")), ("work_authorization_notes", Type("string")));
    }

    public static string BuildProfilePrompt(string text, string languageName) => $"""
You extract a structured, EVIDENCE-BACKED profile from a resume written primarily in {languageName}.

HARD RULES
- Every fact must carry a `quote`: a SHORT contiguous fragment (max ~200 characters) copied VERBATIM from the resume text below. Do not paraphrase inside quotes. Facts without a verbatim quote will be discarded by the program.
- Never invent employers, dates, skills, tools, metrics or claims. If unsure, omit the item.
- Include EVERY role (newest first), including non-IT roles such as administrative assistant. Do not drop any.
- roles[].start / roles[].end use "YYYY-MM" (or "YYYY" if only the year is known, written as "YYYY-01" for start and "YYYY-12" for end). Use "present" as end for an ongoing role. Use "" when the resume gives no date. Never guess dates.
- roles[].quote = the line(s) naming the title/employer/period. roles[].skills_used = canonical skill names used in that role, only if the resume ties them to it.
- skills[]: every technology, language, framework, tool, platform, database, methodology or domain explicitly named. `canonical` = lowercase, no version, no punctuation variants (e.g. "node.js" -> "nodejs", "Postgres" -> "postgresql"). `quotes` = where it appears.
- transferable_skills[]: abstract capabilities the candidate DEMONSTRATED in any role, including non-technical ones (e.g. user support, document/process control, spreadsheets and reporting, routine automation, communication with clients/suppliers, scheduling, financial/administrative routines). `from_roles` = titles of the roles where shown. Each needs a verbatim quote.
- languages[]: spoken/written languages with the stated level only if present.
- headline: one positioning sentence in {languageName}. professional_profile: 2-4 dense paragraphs in {languageName} covering the FULL career arc (all roles).
- seniority: a short free-text label of the candidate's realistic level, based on actual dated experience.
- Do NOT compute years of experience; the program does it from dates.

Resume text:
{Text.Take(text, 40000)}

""";

    // ------------------------------------------------------------ formato legado

    private static string S(JsonNode? node) => Text.PyStr(node);
    private static IEnumerable<JsonNode?> Arr(JsonNode? node) => node is JsonArray array ? array : [];
    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    private static string Category(string category) => category switch
    {
        "tool" or "cloud" => "tools", "soft" => "soft_skills", "domain" => "domains", _ => "technical_skills",
    };

    /// <summary>Formato antigo de <c>analysis_json</c> (painel, assistente, regras de formulário).</summary>
    public static JsonObject LegacyAnalysis(JsonObject profile, string language)
    {
        JsonArray Pick(string key, Func<JsonObject, string> pick) => Strings(Arr(profile[key]).OfType<JsonObject>().Select(pick));
        var technical = new JsonArray(); var tools = new JsonArray(); var soft = new JsonArray(); var domains = new JsonArray();
        var experience = new JsonArray();
        var output = new JsonObject
        {
            ["headline"] = S(profile["headline"]), ["professional_profile"] = S(profile["professional_profile"]), ["seniority"] = S(profile["seniority"]),
            ["technical_skills"] = technical, ["tools"] = tools, ["soft_skills"] = soft, ["domains"] = domains, ["experience"] = experience,
            ["projects"] = Pick("projects", p => S(p["text"])), ["achievements"] = Pick("achievements", a => S(a["text"])),
            ["education"] = Pick("education", e => S(e["text"])), ["certifications"] = Pick("certifications", c => S(c["text"])),
            ["languages"] = Pick("languages", i => $"{S(i["language"])} {S(i["level"])}".Trim()),
            ["work_authorization_notes"] = S(profile["work_authorization_notes"]), ["location_notes"] = S(profile["location_notes"]),
        };
        foreach (var skill in Arr(profile["skills"]).OfType<JsonObject>())
        {
            var target = Category(S(skill["category"])) switch { "tools" => tools, "soft_skills" => soft, "domains" => domains, _ => technical };
            target.Add(S(skill["name"]));
        }
        var total = JobAssessment.Number((profile["derived"] as JsonObject)?["total_months"]);
        if (total != 0)
        {
            var years = Text.PyRound(total / 12.0, 1).ToString("0.0", CultureInfo.InvariantCulture);
            output["years_of_experience"] = (language == "pt" ? $"{years} anos" : $"{years} years") + " (calculado das datas)";
        }
        else output["years_of_experience"] = "";
        var roles = Arr(profile["roles"]).OfType<JsonObject>().ToList();
        foreach (var role in roles)
        {
            var start = Text.Truthy(role["start"]) ? S(role["start"]) : "?";
            var end = Text.Truthy(role["end"]) ? S(role["end"]) : "?";
            experience.Add(new JsonObject
            {
                ["role"] = S(role["title"]), ["employer"] = S(role["employer"]), ["period"] = $"{start} – {end}", ["is_current"] = S(role["end"]) == "present",
                ["responsibilities"] = Strings(Arr(role["responsibilities"]).OfType<JsonObject>().Select(r => S(r["text"]))), ["highlights"] = new JsonArray(),
            });
        }
        if (roles.FirstOrDefault(r => S(r["end"]) == "present") is { } current) output["current_employer"] = S(current["employer"]);
        var coverage = profile["coverage"] as JsonObject ?? [];
        var kept = coverage["kept"] is null ? "0" : Text.PyStrRaw(coverage["kept"]);
        var ungrounded = coverage["ungrounded"] is null ? "0" : Text.PyStrRaw(coverage["ungrounded"]);
        output["coverage_notes"] = language == "pt"
            ? $"{kept} fatos verificados no texto do PDF; {ungrounded} descartados por falta de citação."
            : $"{kept} facts verified against the PDF text; {ungrounded} discarded for lacking a quote.";
        return output;
    }

    // ------------------------------------------------------------ dossiê

    [GeneratedRegex(@"\batual\b|\bpresent\b|\bcurrent\b|\bhoje\b|\buntil\s+now\b|\bto\s+present\b|\baté\s+o\s+momento\b|is_current\s*[:=]\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentRegex();

    /// <summary>Papel atual a partir de período/texto (Atual, Present, Current…).</summary>
    public static bool ExperienceIsCurrent(JsonNode? item)
    {
        string hay;
        if (item is JsonObject o)
            hay = string.Join(' ', S(o["period"]), S(o["role"]), S(o["employer"]), Text.Truthy(o["is_current"]) ? Text.PyStrRaw(o["is_current"]) : "");
        else hay = Text.Truthy(item) ? Text.PyStrRaw(item) : "";
        return CurrentRegex().IsMatch(hay);
    }

    /// <summary>Equivale ao <c>repr()</c> de um dict/valor simples do Python (só aparece quando um cargo não tem título nem empresa).</summary>
    internal static string PyRepr(JsonNode? node)
    {
        switch (node)
        {
            case null: return "None";
            case JsonObject o: return "{" + string.Join(", ", o.Select(kv => $"{PyRepr(JsonValue.Create(kv.Key))}: {PyRepr(kv.Value)}")) + "}";
            case JsonArray a: return "[" + string.Join(", ", a.Select(PyRepr)) + "]";
            case JsonValue v when v.TryGetValue<string>(out var s):
            {
                var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
                var sb = new StringBuilder().Append(quote);
                foreach (var c in s)
                {
                    if (c == '\\') sb.Append("\\\\");
                    else if (c == quote) sb.Append('\\').Append(c);
                    else if (c == '\n') sb.Append("\\n");
                    else if (c == '\r') sb.Append("\\r");
                    else if (c == '\t') sb.Append("\\t");
                    else sb.Append(c);
                }
                return sb.Append(quote).ToString();
            }
            default: return Text.PyStrRaw(node);
        }
    }

    public static string FormatExperienceItem(JsonNode? item)
    {
        if (item is JsonObject o)
        {
            var role = S(o["role"]).Trim();
            var employer = S(o["employer"]).Trim();
            var period = S(o["period"]).Trim();
            var head = string.Join(" — ", new[] { role, employer }.Where(p => p.Length > 0));
            if (head.Length == 0) head = Text.Take(PyRepr(o), 220);
            if (period.Length > 0) head = $"{head} ({period})";
            if (ExperienceIsCurrent(o)) head = $"[CURRENT ROLE] {head}";
            var parts = new List<string> { head };
            foreach (var key in new[] { "responsibilities", "highlights" })
            {
                var value = o[key];
                if (value is JsonArray lines)
                {
                    foreach (var line in lines)
                    {
                        var text = Text.PyStrRaw(line).Trim();
                        if (text.Length > 0) parts.Add($"  - {Text.Take(text, 300)}");
                    }
                }
                else if (S(value).Trim().Length > 0) parts.Add($"  - {Text.Take(S(value).Trim(), 300)}");
            }
            return string.Join('\n', parts);
        }
        var plain = Text.PyStrRaw(item).Trim();
        return ExperienceIsCurrent(JsonValue.Create(plain)) ? $"[CURRENT ROLE] {plain}" : plain;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> DossierLabels = new()
    {
        ["pt"] = new()
        {
            ["headline"] = "Headline", ["profile"] = "Perfil profissional", ["level"] = "Senioridade", ["years"] = "Anos de experiência",
            ["tech"] = "Competências técnicas", ["tools"] = "Ferramentas e plataformas", ["soft"] = "Soft skills", ["experience"] = "Experiência profissional (todas as funções)",
            ["projects"] = "Projetos e automações relevantes", ["achievements"] = "Conquistas / resultados mensuráveis", ["education"] = "Formação",
            ["certs"] = "Certificações", ["languages"] = "Idiomas", ["domains"] = "Domínios / indústrias", ["auth"] = "Notas de autorização de trabalho",
            ["location"] = "Localidade / mobilidade", ["coverage"] = "Cobertura da extração",
        },
        ["en"] = new()
        {
            ["headline"] = "Headline", ["profile"] = "Professional profile", ["level"] = "Seniority", ["years"] = "Years of experience",
            ["tech"] = "Technical skills", ["tools"] = "Tools and platforms", ["soft"] = "Soft skills", ["experience"] = "Professional experience (all roles)",
            ["projects"] = "Relevant projects and automations", ["achievements"] = "Achievements / measurable results", ["education"] = "Education",
            ["certs"] = "Certifications", ["languages"] = "Languages", ["domains"] = "Domains / industries", ["auth"] = "Work authorization notes",
            ["location"] = "Location / mobility", ["coverage"] = "Extraction coverage",
        },
    };

    /// <summary>Dossiê estruturado (rótulos no idioma do currículo). Experiência vem antes das listas de skills, para que cortes por tamanho percam skills repetidas.</summary>
    public static string ComposeDossier(JsonObject analysis, string language)
    {
        var label = DossierLabels[language == "pt" ? "pt" : "en"];
        var languageName = language == "pt" ? "Português" : "English";
        var lines = new List<string>();
        string First(params string[] keys) => keys.Select(k => S(analysis[k])).FirstOrDefault(v => v.Length > 0) ?? "";
        var headline = First("headline", "summary").Trim();
        if (headline.Length > 0) lines.Add($"{label["headline"]}: {headline}");
        var profile = S(analysis["professional_profile"]).Trim();
        if (profile.Length > 0) lines.Add($"{label["profile"]}:\n{profile}");
        var seniority = S(analysis["seniority"]).Trim();
        var years = S(analysis["years_of_experience"]).Trim();
        if (seniority.Length > 0 || years.Length > 0)
            lines.Add($"{label["level"]}: {(seniority.Length > 0 ? seniority : "—")} | {label["years"]}: {(years.Length > 0 ? years : "—")}");

        void Section(string title, JsonNode? value)
        {
            if (value is JsonArray array)
            {
                var items = array.Select(v => Text.PyStrRaw(v).Trim()).Where(v => v.Length > 0).ToList();
                if (items.Count > 0) lines.Add(title + ":\n- " + string.Join("\n- ", items));
            }
            else
            {
                var text = S(value).Trim();
                if (text.Length > 0) lines.Add($"{title}:\n{text}");
            }
        }

        if (analysis["experience"] is JsonArray experience && experience.Count > 0)
        {
            var formatted = experience.Select(FormatExperienceItem).Where(f => f.Trim().Length > 0).ToList();
            if (formatted.Count > 0) lines.Add($"{label["experience"]}:\n- " + string.Join("\n- ", formatted));
        }
        Section(label["tech"], Text.Truthy(analysis["technical_skills"]) ? analysis["technical_skills"] : analysis["skills"]);
        Section(label["tools"], analysis["tools"]);
        Section(label["soft"], analysis["soft_skills"]);
        Section(label["projects"], analysis["projects"]);
        Section(label["achievements"], analysis["achievements"]);
        Section(label["education"], analysis["education"]);
        Section(label["certs"], analysis["certifications"]);
        Section(label["languages"], analysis["languages"]);
        Section(label["domains"], analysis["domains"]);
        Section(label["auth"], analysis["work_authorization_notes"]);
        Section(label["location"], analysis["location_notes"]);
        var coverage = S(analysis["coverage_notes"]).Trim();
        if (coverage.Length > 0) lines.Add($"{label["coverage"]}:\n{coverage}");
        var dossier = string.Join("\n\n", lines).Trim();
        if (dossier.Length == 0) dossier = headline.Length > 0 ? headline : $"Análise sem conteúdo estruturado ({languageName}).";
        return Text.Take(dossier, 20000);
    }

    // ------------------------------------------------------------ análise

    /// <summary>Chamada de IA da análise: (provedor, modelo, chave, prompt) → JSON do perfil bruto.</summary>
    public delegate Task<JsonObject> ProfileCall(string provider, string model, string apiKey, string prompt, CancellationToken ct);

    /// <summary>
    /// Extrai o perfil canônico ancorado e devolve o formato legado de <c>analysis_json</c>; a chave <c>_profile</c> carrega o perfil
    /// verificado (gravado em <c>resumes.profile_json</c>).
    /// </summary>
    public static async Task<JsonObject> AnalyzeAsync(string text, string language, string provider, string model, string apiKey, AiClient? ai,
        ProfileCall? callJson = null, DateOnly? today = null, CancellationToken ct = default)
    {
        var languageName = language == "pt" ? "Portuguese" : "English";
        var prompt = BuildProfilePrompt(text, languageName);
        JsonObject raw;
        if (callJson is not null) raw = await callJson(provider, model, apiKey, prompt, ct);
        else
        {
            if (string.IsNullOrEmpty(apiKey)) throw new AiUnavailableException("Configure a chave da API de IA antes de analisar o currículo.");
            if (ai is null) throw new InvalidOperationException("Cliente de IA ausente.");
            raw = await ai.JsonAsync(prompt, ProfileSchema(), new AiCall(provider.ToLowerInvariant().Trim(), model, apiKey),
                maxOutputTokens: 12000, thinkingBudget: 2048, timeoutSeconds: 180, ct: ct);
        }
        var profile = ResumeProfile.GroundProfile(raw, text, today);
        if (((JsonArray)profile["roles"]!).Count == 0 && ((JsonArray)profile["skills"]!).Count == 0)
            throw new InvalidOperationException("A análise não retornou fatos verificáveis no texto do PDF.");
        var analysis = LegacyAnalysis(profile, language);
        var summary = ComposeDossier(analysis, language);
        analysis["summary"] = summary;
        if (S(analysis["headline"]).Trim().Length == 0) analysis["headline"] = Text.Take(summary.Split('\n', 2)[0], 240);
        analysis["_profile"] = profile;
        return analysis;
    }
}
