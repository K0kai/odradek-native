using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Odradek.Core.Candidate;

/// <summary>Campos da vaga que a triagem lê (o <c>dict(row)</c> do Python).</summary>
public sealed record AssessJob(string Title, string Company, string Location, string Description, string Language = "", string SearchRemote = "");

/// <summary>Chamada de IA que devolve JSON conforme o schema (o chamador resolve provedor, modelo e chave).</summary>
public delegate Task<JsonObject> CallJson(string prompt, JsonObject schema, int maxOutputTokens, int? thinkingBudget, CancellationToken ct);

/// <summary>
/// Triagem v2 (porte de <c>jobs/job_assessment.py</c>): a IA só extrai requisitos, responsabilidades e correspondências e CITA;
/// o código descarta o que não tem citação verificável, calcula o score, aplica bloqueios e só então aprova.
/// </summary>
public static partial class JobAssessment
{
    public const string ScoringVersion = "v2.1";

    private const double WeightMust = 0.70, WeightNice = 0.15, WeightFit = 0.15;
    private const double DifferentWithStrongTransfer = 0.5, YearsTolerance = 1.0, InflatedMajority = 0.5;
    private const double TransferCoreCoverage = 0.6, TransferableCovRelax = 0.10, MajorObjectionPenalty = 0.10, MajorObjectionCap = 0.30;
    private const int KitchenSinkMusts = 10;
    private static readonly (double X, double Y)[] OptimismCurve = [(0, 1.00), (50, 0.80), (75, 0.70), (100, 0.55)];
    private static readonly HashSet<string> NeverInflated = ["location", "authorization", "language", "clearance"];
    private static readonly string[] Categories = ["skill", "experience_years", "seniority", "degree", "language", "location", "authorization", "clearance", "other"];
    private static readonly HashSet<string> Stopwords = new("a o e de do da dos das em no na nos nas um uma para por com sem ou que se ao as os the and or of to in for with on at an be is are will you your our we their this that from as by".Split(' '));
    private static readonly Dictionary<string, string> LanguageAliases = new()
    {
        ["ingles"] = "english", ["english"] = "english", ["portugues"] = "portuguese", ["portuguese"] = "portuguese", ["espanhol"] = "spanish", ["spanish"] = "spanish",
        ["frances"] = "french", ["french"] = "french", ["alemao"] = "german", ["german"] = "german", ["italiano"] = "italian", ["italian"] = "italian",
    };
    private static readonly Dictionary<string, string> CountryAliases = new()
    {
        ["brasil"] = "brazil", ["br"] = "brazil", ["brazil"] = "brazil", ["eua"] = "united states", ["usa"] = "united states", ["us"] = "united states",
        ["united states of america"] = "united states", ["uk"] = "united kingdom", ["reino unido"] = "united kingdom",
    };

    private static double Credit(string status) => status switch { "met" => 1.0, "transferable" => 0.75, "partial" => 0.5, _ => 0.0 };
    private static readonly string[] Statuses = ["met", "transferable", "partial", "unmet", "unknown"];

    // ------------------------------------------------------------ schemas

    private static JsonObject Prop(string type, params string[] choices)
    {
        var node = new JsonObject { ["type"] = type };
        if (choices.Length > 0) node["enum"] = new JsonArray(choices.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        return node;
    }

    private static JsonObject Obj(params (string Key, JsonNode Value)[] properties)
    {
        var props = new JsonObject();
        foreach (var (key, value) in properties) props[key] = value;
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    private static JsonObject ArrayOf(JsonNode items) => new() { ["type"] = "array", ["items"] = items };

    public static JsonObject AssessSchema() => Obj(
        ("requirements", ArrayOf(Obj(
            ("id", Prop("string")), ("text", Prop("string")), ("kind", Prop("string", "must", "nice")),
            ("category", Prop("string", Categories)), ("years_required", Prop("number")), ("education_credit_years", Prop("number")),
            ("job_quote", Prop("string")), ("realism", Prop("string", "core", "inflated")),
            ("realism_basis_quote", Prop("string")), ("realism_reason", Prop("string"))))),
        ("role_core", Obj(
            ("level_label", Prop("string")), ("level_quote", Prop("string")),
            ("responsibilities", ArrayOf(Obj(("id", Prop("string")), ("text", Prop("string")), ("job_quote", Prop("string"))))))),
        ("matches", ArrayOf(Obj(
            ("requirement_id", Prop("string")), ("status", Prop("string", Statuses)),
            ("evidence_ids", ArrayOf(Prop("string"))), ("resume_quote", Prop("string")), ("bridge", Prop("string"))))),
        ("stated", Obj(
            ("min_years", Prop("number")), ("min_years_quote", Prop("string")),
            ("workplace", Prop("string", "remote", "hybrid", "onsite", "unspecified")), ("workplace_quote", Prop("string")),
            ("job_country", Prop("string")), ("job_country_quote", Prop("string")), ("job_city", Prop("string")), ("job_city_quote", Prop("string")),
            ("foreign_eligibility", Prop("string", "open_to_candidate", "restricted", "unstated")), ("eligibility_quote", Prop("string")),
            ("authorization", Prop("string", "not_required_or_candidate_has", "candidate_lacks", "unclear")), ("authorization_quote", Prop("string")),
            ("required_languages", ArrayOf(Obj(("language", Prop("string")), ("quote", Prop("string"))))))),
        ("role_family_fit", Prop("string", "same", "adjacent", "different")),
        ("fit_reason", Prop("string")),
        ("cover_letter_required", Prop("boolean")));

    public static JsonObject SkepticSchema() => Obj(
        ("objections", ArrayOf(Obj(
            ("kind", Prop("string", "new_issue", "requirement_unmet", "inflated_is_core")), ("requirement_id", Prop("string")),
            ("severity", Prop("string", "blocker", "major", "minor")), ("job_quote", Prop("string")), ("reason", Prop("string"))))));

    // ------------------------------------------------------------ utilidades

    private static string S(JsonNode? node) => Core.Text.PyStr(node);
    private static IEnumerable<JsonNode?> Arr(JsonNode? node) => node is JsonArray array ? array : [];
    private static string Cfg(IReadOnlyDictionary<string, string> cfg, string key) => cfg.TryGetValue(key, out var value) ? value : "";
    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    /// <summary>Equivale a <c>float(str(x))</c> com padrão 75 quando vazio ou inválido; limitado a 0–100; devolve a cobertura mínima da curva.</summary>
    public static double OptimismThreshold(string? value)
    {
        var optimism = 75.0;
        if (!string.IsNullOrEmpty(value) && double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && !double.IsNaN(parsed))
            optimism = Math.Max(0.0, Math.Min(100.0, parsed));
        for (var i = 0; i < OptimismCurve.Length - 1; i++)
        {
            var (x0, y0) = OptimismCurve[i];
            var (x1, y1) = OptimismCurve[i + 1];
            if (optimism <= x1) return y0 + (y1 - y0) * (optimism - x0) / (x1 - x0);
        }
        return OptimismCurve[^1].Y;
    }

    private static HashSet<string> SignificantTokens(string text) =>
        Core.Text.NormalizeText(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3 && !Stopwords.Contains(t)).ToHashSet();

    private static string Pct0(double value) => Math.Round(value * 100.0, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string Fixed1(double value) => Core.Text.PyRound(value, 1).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary><c>{x:g}</c> do Python para anos exigidos (inteiro sem ".0").</summary>
    private static string General(double value) => value.ToString("G6", CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-");

    // ------------------------------------------------------------ prompts

    public static string BuildAssessPrompt(AssessJob job, string jobText, JsonObject profile, IReadOnlyDictionary<string, string> cfg, string language, string preferences)
    {
        var facts = Cfg(cfg, language == "pt" ? "candidate_facts_pt" : "candidate_facts_en").Trim();
        var where = string.Join(", ", new[] { Cfg(cfg, "candidate_city"), Cfg(cfg, "candidate_state"), Cfg(cfg, "candidate_country") }.Where(p => p.Length > 0));
        return $"""
You audit ONE job posting against ONE candidate profile. You EXTRACT and CITE; a program verifies every quote and computes the score. Never invent.

QUOTES: every `job_quote`, `realism_basis_quote`, `level_quote`, `*_quote` must be a SHORT fragment copied VERBATIM from the JOB TEXT. `resume_quote` must be copied VERBATIM from a quote/text of the candidate PROFILE evidence you cite. Items whose quotes cannot be found are discarded.

1. requirements[]: the posting's requirements (ids Q1, Q2, ...). `kind`=must only when the posting states it as required/mandatory/"X+ years"/obligatório; when in doubt use nice. `category` as listed.
   Duration requirements (category experience_years, e.g. "5+ years of Python"): set `years_required` = the years asked FOR THAT requirement (0 for any other requirement). Set `education_credit_years` only when the posting says a degree/course can replace or add to experience for it (e.g. "3 years of experience or a related degree"), with the years it grants; otherwise 0. Never compute the candidate's years: the program does it from the dated roles.
2. role_core: what the person will ACTUALLY DO day to day (responsibilities J1, J2, ... from the duties section, not the requirements list) and the posting's stated level label (intern/junior/senior...) with its quote.
3. REALISM: judge each requirement against the posting ITSELF. Mark `inflated` only when the posting contradicts or does not support it, and give `realism_basis_quote` = verbatim quote from the posting that shows the contradiction (e.g. an "Internship" title next to "5+ years", a junior role demanding architect-level skills, a requirement unrelated to any listed duty). Otherwise `core`. location/authorization/language/clearance requirements are never inflated.
4. matches[]: one per requirement id (Q*) AND per responsibility id (J*). status:
   - met: the profile directly shows it. transferable: not done exactly, but the profile shows an equivalent capability (explain in `bridge`, one sentence). partial: related but incomplete. unmet: no evidence. unknown: cannot tell.
   - `evidence_ids`: ids from the profile ([R1], [R1.2], [S3], [T1]...). `resume_quote`: a SHORT fragment copied verbatim from the cited evidence's own text (a role line, a responsibility, a project, a certification...). For a SKILL id ([S*]) quote ONLY the skill name (e.g. "PostgreSQL", "React"): NEVER the usage statistics after the colon ("5m, last 2026-11", "portfolio projects 3m (P1)") and never a paraphrase, because those are not in the resume. met/transferable/partial WITHOUT valid evidence ids will be downgraded to unmet; a quote that cannot be found downgrades met/transferable to partial.
   - Credit transferable capabilities generously but only when truly equivalent and evidenced (e.g. internal user support -> helpdesk, spreadsheet reporting -> data analysis).
   - Portfolio projects ([P*] items, or a skill line saying "portfolio projects") are real evidence the candidate can use that skill: they may support met/partial for a SKILL requirement. They do NOT count as professional experience: never use them to satisfy "X years of experience" requirements.
   - For an experience_years requirement, cite as `evidence_ids` the skill ([S*]) or role ([R*]) the duration must be measured on; the program checks the dated months against `years_required` and downgrades the status when the dates do not support it. With no skill or role cited, total professional experience is used.
5. stated: min_years (number, 0 if none) with quote; workplace (remote/hybrid/onsite) with `workplace_quote`, or "unspecified" when the posting does not say; job_country and job_city where the job is based, each with a quote from the JOB TEXT or the Location line (empty when not stated); foreign_eligibility for the candidate living in {(where.Length > 0 ? where : "an unknown location")}: open_to_candidate ONLY when the posting explicitly says people there (or anyone/worldwide/"work from anywhere"/the candidate's region) can apply, restricted when it limits applicants to places that exclude the candidate, otherwise unstated; always with `eligibility_quote`. Do not guess any of these. authorization = candidate_lacks ONLY if the posting requires work authorization the candidate does not have per the profile, with quote; required_languages (languages the posting requires for the job, with quotes).
6. role_family_fit: same (same occupation family as candidate's roles), adjacent (neighbouring family, skills clearly overlap), different. Be honest; adjacent/different is acceptable when transferable capabilities exist.
7. cover_letter_required: true only if the posting explicitly asks for a cover letter.

CANDIDATE PREFERENCES (judgement guidance, never overrides the rules above):
{preferences}

CANDIDATE FACTS: {(facts.Length > 0 ? facts : "[none]")}

CANDIDATE PROFILE (evidence ids in brackets):
{ResumeProfile.CompactProfileText(profile, 9500)}

JOB
Title: {job.Title}
Company: {job.Company}
Location: {job.Location}
JOB TEXT:
{Core.Text.Take(jobText, 14000)}

""";
    }

    public static string BuildSkepticPrompt(string jobText, JsonObject profile, Verified verified)
    {
        var lines = verified.Requirements.Select(r => $"- {S(r["id"])} [{S(r["kind"])}/{S(r["realism"])}] {S(r["text"])} => {S(r["status"])}");
        return $"""
You are a SKEPTICAL reviewer. A screening already APPROVED this application. Find concrete, CITED reasons it should NOT go ahead. Do not invent problems; no quote, no objection.

Rules: `job_quote` must be copied VERBATIM from the JOB TEXT. severity: blocker = the candidate clearly cannot do/qualify for the job; major = a significant uncovered core requirement; minor = small gap.
kind: requirement_unmet (a requirement id the screening marked met/transferable/partial that the profile does not really support), new_issue (a problem the screening missed), inflated_is_core (a requirement the screening called inflated that is actually central to the duties - cite the duty).
Never use a requirement marked `inflated` as a blocker unless kind is inflated_is_core with proof.

SCREENING RESULT
{string.Join("\n", lines)}

CANDIDATE PROFILE
{ResumeProfile.CompactProfileText(profile, 6000)}

JOB TEXT
{Core.Text.Take(jobText, 12000)}

""";
    }

    // ------------------------------------------------------------ verificação

    /// <summary>Resultado da verificação das citações: listas já normalizadas, prontas para decidir.</summary>
    public sealed class Verified
    {
        public List<JsonObject> Requirements { get; init; } = [];
        public List<JsonObject> Responsibilities { get; init; } = [];
        public string LevelLabel { get; init; } = "";
        public JsonObject Stated { get; init; } = [];
        public string RoleFamilyFit { get; init; } = "different";
        public string FitReason { get; init; } = "";
        public bool CoverLetterRequired { get; init; }
        public JsonObject Dropped { get; init; } = [];
    }

    private static IEnumerable<string> QuoteVariants(string quote)
    {
        yield return quote;
        foreach (var separator in new[] { ":", ";", " — ", " - " })
        {
            var cut = quote.IndexOf(separator, StringComparison.Ordinal);
            var head = (cut < 0 ? quote : quote[..cut]).Trim();
            if (head.Length > 0 && head != quote) yield return head;
        }
    }

    private static bool QuoteSupported(string quote, NormalizedText resumeNorm, List<string> evidence, Dictionary<string, ResumeProfile.Evidence> index)
    {
        if (quote.Length == 0) return false;
        // Nome curto de skill ("C#", "Go", "R") é rejeitado por QuoteInText (mínimo de 3 caracteres): vale quando é exatamente o nome de uma skill citada.
        var shortQuote = Core.Text.NormalizeText(quote);
        if (shortQuote.Length > 0 && evidence.Any(e => index[e].Kind == "skill" && Core.Text.NormalizeText(index[e].Text) == shortQuote)) return true;
        var blob = NormalizedText.Of(string.Join(" ", evidence.Select(e => $"{index[e].Text} {string.Join(" ", index[e].Quotes)}")));
        return QuoteVariants(quote).Any(v => ResumeProfile.QuoteInText(v, resumeNorm) || ResumeProfile.QuoteInText(v, blob));
    }

    private static string Cut(JsonNode? value, int limit) => Core.Text.Take(S(value), limit);

    private const double MaxRequiredYears = 40, MaxEducationCreditYears = 10, DurationToleranceYears = 0.25;

    /// <summary>"2 anos e 3 meses" (o tempo comprovado que aparece no cartão e no aviso).</summary>
    public static string Duration(long months)
    {
        var (years, rest) = (months / 12, months % 12);
        var parts = new List<string>();
        if (years > 0) parts.Add($"{years} ano{(years == 1 ? "" : "s")}");
        if (rest > 0 || years == 0) parts.Add($"{rest} {(rest == 1 ? "mês" : "meses")}");
        return string.Join(" e ", parts);
    }

    /// <summary>
    /// Confere, pelas datas do currículo, as exigências de tempo ("5+ anos de Python"). O tempo vem das skills e cargos citados como evidência (ou da
    /// experiência total, sem citação) e, quando a vaga aceita curso no lugar de experiência, soma a formação até o limite que ela concede. Se o
    /// tempo comprovado não sustenta o status dado pela IA, o status cai: com ao menos metade do exigido vira parcial, com menos vira não atende.
    /// Cargos sem datas não provam nada contra o candidato: nesse caso o status fica e o aviso explica.
    /// </summary>
    private static void CheckDurations(IEnumerable<JsonObject> requirements, JsonObject profile, Action downgraded)
    {
        var asked = requirements.Where(r => r["years_required"] is not null && S(r["realism"]) != "inflated").ToList();
        if (asked.Count == 0) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var skillStats = (profile["derived"] as JsonObject)?["skills"] as JsonObject ?? [];
        var roleIds = Arr(profile["roles"]).OfType<JsonObject>().Select(r => S(r["id"])).ToList();
        var totalMonths = ResumeProfile.MonthsForRoles(profile, roleIds, today);
        var educationMonths = ResumeProfile.EducationMonths(profile, today);
        var undated = ResumeProfile.HasUndatedRoles(profile, today);

        foreach (var req in asked)
        {
            var required = Number(req["years_required"]);
            var measured = new HashSet<string>();
            var specific = false;
            foreach (var id in Arr(req["evidence_ids"]).Select(S))
            {
                if (skillStats[id] is JsonObject stat) { specific = true; foreach (var role in Arr(stat["role_ids"])) measured.Add(S(role)); }
                else if (id.Length > 1 && id[0] == 'R') { specific = true; measured.Add(id.Split('.')[0]); }
            }
            var months = specific ? ResumeProfile.MonthsForRoles(profile, measured, today) : totalMonths;
            var credit = Math.Min(Number(req["education_credit_years"]) * 12, educationMonths);
            var proven = (int)Math.Round(months + credit);
            req["proven_months"] = proven;

            var status = S(req["status"]);
            if (status is not ("met" or "transferable" or "partial") || proven / 12.0 + DurationToleranceYears >= required) continue;
            if (undated && months == 0)
            {
                req["note"] = "o currículo tem cargos sem datas: não foi possível medir o tempo de experiência";
                continue;
            }
            var ratio = proven / 12.0 / required;
            var target = status == "partial" ? (ratio < 0.5 ? "unmet" : "partial") : (ratio >= 0.5 ? "partial" : "unmet");
            if (target != status) { req["status"] = target; req["bridge"] = ""; downgraded(); }
            req["note"] = $"exige {General(required)}+ anos; comprovado {(proven == 0 ? "nenhum tempo" : Duration(proven))}";
        }
    }

    public static Verified Verify(JsonObject raw, NormalizedText jobNorm, NormalizedText resumeNorm, JsonObject profile)
    {
        var index = ResumeProfile.EvidenceIndex(profile);
        var dropped = new JsonObject { ["requirements"] = 0, ["responsibilities"] = 0, ["matches_downgraded"] = 0, ["realism_reverted"] = 0 };
        void Bump(string key) => dropped[key] = dropped[key]!.GetValue<int>() + 1;
        bool Grounded(JsonNode? quote) => quote is JsonValue v && v.TryGetValue<string>(out var text) && ResumeProfile.QuoteInText(text, jobNorm);

        var requirements = new List<JsonObject>();
        var seen = new HashSet<string>();
        foreach (var item in Arr(raw["requirements"]).OfType<JsonObject>())
        {
            var rid = S(item["id"]).Trim();
            if (rid.Length == 0 || seen.Contains(rid) || S(item["text"]).Trim().Length == 0 || !Grounded(item["job_quote"])) { Bump("requirements"); continue; }
            seen.Add(rid);
            var realism = S(item["realism"]) is "core" or "inflated" ? S(item["realism"]) : "core";
            var category = Categories.Contains(S(item["category"])) ? S(item["category"]) : "other";
            var reason = Cut(item["realism_reason"], 300);
            if (realism == "inflated" && (NeverInflated.Contains(category) || !Grounded(item["realism_basis_quote"])))
            {
                realism = "core";
                reason = "";
                Bump("realism_reverted");
            }
            var requirement = new JsonObject
            {
                ["id"] = rid, ["text"] = Cut(item["text"], 300), ["kind"] = S(item["kind"]) == "must" ? "must" : "nice",
                ["category"] = category, ["job_quote"] = Cut(item["job_quote"], 300), ["realism"] = realism,
                ["realism_basis_quote"] = realism == "inflated" ? Cut(item["realism_basis_quote"], 300) : "", ["realism_reason"] = reason,
            };
            if (category == "experience_years" && Math.Min(ParseYears(item["years_required"]), MaxRequiredYears) is var yearsAsked and > 0)
            {
                requirement["years_required"] = yearsAsked; // só quando a vaga pede um tempo: as demais exigências mantêm o formato de antes
                if (Math.Min(ParseYears(item["education_credit_years"]), MaxEducationCreditYears) is var credit and > 0) requirement["education_credit_years"] = credit;
            }
            requirements.Add(requirement);
        }

        var roleCore = raw["role_core"] as JsonObject ?? [];
        var responsibilities = new List<JsonObject>();
        foreach (var item in Arr(roleCore["responsibilities"]).OfType<JsonObject>())
        {
            var rid = S(item["id"]).Trim();
            if (rid.Length > 0 && !seen.Contains(rid) && S(item["text"]).Trim().Length > 0 && Grounded(item["job_quote"]))
            {
                seen.Add(rid);
                responsibilities.Add(new JsonObject { ["id"] = rid, ["text"] = Cut(item["text"], 300), ["job_quote"] = Cut(item["job_quote"], 300) });
            }
            else Bump("responsibilities");
        }
        var levelLabel = Grounded(roleCore["level_quote"]) ? Cut(roleCore["level_label"], 80) : "";

        // kitchen sink: muitos obrigatórios de skill desligados de qualquer responsabilidade → lista de desejos
        var respTokens = responsibilities.SelectMany(r => SignificantTokens(S(r["text"]) + " " + S(r["job_quote"]))).ToHashSet();
        var coreSkillMusts = requirements.Where(r => S(r["kind"]) == "must" && S(r["realism"]) == "core" && S(r["category"]) is "skill" or "other").ToList();
        if (responsibilities.Count > 0 && coreSkillMusts.Count > KitchenSinkMusts)
        {
            foreach (var req in coreSkillMusts)
            {
                if (!SignificantTokens(S(req["text"])).Overlaps(respTokens))
                {
                    req["realism"] = "inflated";
                    req["realism_reason"] = "lista de desejos: não ligada a nenhuma responsabilidade descrita";
                }
            }
        }

        var matches = new Dictionary<string, JsonObject>();
        var validIds = requirements.Select(r => S(r["id"])).Concat(responsibilities.Select(r => S(r["id"]))).ToHashSet();
        foreach (var item in Arr(raw["matches"]).OfType<JsonObject>())
        {
            var rid = S(item["requirement_id"]).Trim();
            if (!validIds.Contains(rid) || matches.ContainsKey(rid)) continue;
            var status = Statuses.Contains(S(item["status"])) ? S(item["status"]) : "unknown";
            var evidence = Arr(item["evidence_ids"]).Select(e => S(e)).Where(index.ContainsKey).ToList();
            var quote = S(item["resume_quote"]);
            var bridge = Cut(item["bridge"], 300);
            var note = "";
            if (status is "met" or "transferable" or "partial" && evidence.Count == 0)
            {
                status = "unmet";
                note = "sem evidência válida no perfil";
            }
            else if (status is "met" or "transferable" && !QuoteSupported(quote, resumeNorm, evidence, index))
            {
                // A evidência citada existe no perfil verificado: só a transcrição do modelo falhou. Crédito parcial, nunca "não atendido".
                status = "partial";
                note = "citação do currículo não verificada (evidência válida, crédito parcial)";
            }
            else if (status == "transferable" && bridge.Length == 0)
            {
                status = "partial";
                note = "sem ponte explicada";
            }
            if (note.Length > 0)
            {
                bridge = ""; // ponte de um item rebaixado contradiria a marca exibida
                Bump("matches_downgraded");
            }
            matches[rid] = new JsonObject
            {
                ["status"] = status, ["evidence_ids"] = Strings(evidence.Take(6)), ["resume_quote"] = Core.Text.Take(quote, 300), ["bridge"] = bridge, ["note"] = note,
            };
        }
        foreach (var rid in validIds)
            matches.TryAdd(rid, new JsonObject { ["status"] = "unknown", ["evidence_ids"] = new JsonArray(), ["resume_quote"] = "", ["bridge"] = "", ["note"] = "" });
        foreach (var item in requirements.Concat(responsibilities))
        {
            var match = matches[S(item["id"])];
            item["status"] = match["status"]!.DeepClone();
            item["evidence_ids"] = match["evidence_ids"]!.DeepClone();
            item["bridge"] = match["bridge"]!.DeepClone();
            item["note"] = match["note"]!.DeepClone();
            item["resume_quote"] = match["resume_quote"]!.DeepClone();
        }
        CheckDurations(requirements, profile, () => Bump("matches_downgraded"));

        var statedRaw = raw["stated"] as JsonObject ?? [];
        var workplace = S(statedRaw["workplace"]) is "remote" or "hybrid" or "onsite" ? S(statedRaw["workplace"]) : "unspecified";
        if (workplace != "unspecified" && !Grounded(statedRaw["workplace_quote"])) workplace = "unspecified"; // modelo de trabalho sem citação verificável não conta
        var stated = new JsonObject
        {
            ["min_years"] = 0.0, ["workplace"] = workplace,
            ["workplace_quote"] = workplace != "unspecified" ? Cut(statedRaw["workplace_quote"], 200) : "",
            ["job_country"] = "", ["job_city"] = "", ["foreign_eligibility"] = "unstated", ["eligibility_quote"] = "",
            ["authorization"] = "unclear", ["authorization_quote"] = "", ["required_languages"] = new JsonArray(),
        };
        foreach (var key in new[] { "job_country", "job_city" })
        {
            var value = Core.Text.Take(S(statedRaw[key]).Trim(), 80);
            if (value.Length > 0 && Grounded(statedRaw[$"{key}_quote"])) stated[key] = value;
        }
        if (S(statedRaw["foreign_eligibility"]) is "open_to_candidate" or "restricted" && Grounded(statedRaw["eligibility_quote"]))
        {
            stated["foreign_eligibility"] = S(statedRaw["foreign_eligibility"]);
            stated["eligibility_quote"] = Cut(statedRaw["eligibility_quote"], 300);
        }
        var years = ParseYears(statedRaw["min_years"]);
        if (years > 0 && Grounded(statedRaw["min_years_quote"]))
        {
            stated["min_years"] = years;
            stated["min_years_quote"] = Cut(statedRaw["min_years_quote"], 200);
        }
        if (S(statedRaw["authorization"]) == "candidate_lacks" && Grounded(statedRaw["authorization_quote"]))
        {
            stated["authorization"] = "candidate_lacks";
            stated["authorization_quote"] = Cut(statedRaw["authorization_quote"], 300);
        }
        foreach (var item in Arr(statedRaw["required_languages"]).OfType<JsonObject>())
            if (S(item["language"]).Trim().Length > 0 && Grounded(item["quote"]))
                ((JsonArray)stated["required_languages"]!).Add(new JsonObject { ["language"] = Cut(item["language"], 40), ["quote"] = Cut(item["quote"], 200) });

        var fit = S(raw["role_family_fit"]) is "same" or "adjacent" or "different" ? S(raw["role_family_fit"]) : "different";
        return new Verified
        {
            Requirements = requirements, Responsibilities = responsibilities, LevelLabel = levelLabel, Stated = stated, RoleFamilyFit = fit,
            FitReason = Cut(raw["fit_reason"], 400), CoverLetterRequired = Core.Text.Truthy(raw["cover_letter_required"]), Dropped = dropped,
        };
    }

    /// <summary>Número JSON como double, venha ele do disco (JsonElement) ou criado em C# (int, long, double): <c>TryGetValue&lt;double&gt;</c> não converte int.</summary>
    internal static double Number(JsonNode? node) =>
        node is JsonValue v && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0.0;

    /// <summary><c>float(stated_raw.get("min_years") or 0)</c>: número, texto numérico ou 0.</summary>
    private static double ParseYears(JsonNode? node)
    {
        if (!Core.Text.Truthy(node)) return 0.0;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (v.TryGetValue<bool>(out var b)) return b ? 1.0 : 0.0;
        }
        return 0.0;
    }

    // ------------------------------------------------------------ decisão

    private static double Coverage(IReadOnlyCollection<JsonObject> items) => items.Count == 0 ? 0.0 : items.Sum(i => Credit(S(i["status"]))) / items.Count;

    private static HashSet<string> CandidateLanguages(JsonObject profile) =>
        Arr(profile["languages"]).OfType<JsonObject>().Select(item =>
        {
            var key = Core.Text.NormalizeText(S(item["language"]));
            return LanguageAliases.GetValueOrDefault(key, key);
        }).ToHashSet();

    private static string CountryKey(string? value)
    {
        var key = Core.Text.NormalizeText(value);
        return CountryAliases.GetValueOrDefault(key, key);
    }

    [GeneratedRegex(@"\bbrazil\b|\bbrasil\b|\bbelo horizonte\b|\bsao paulo\b|\bsão paulo\b|\brio de janeiro\b|\bcuritiba\b|\bbrasilia\b|\brasília\b|\brecife\b|\bporto alegre\b", RegexOptions.IgnoreCase)]
    private static partial Regex BrazilHint();

    /// <summary>País do candidato: a configuração do painel; senão "Brazil" quando a cidade lembra o Brasil (<c>infer_candidate_country</c>).</summary>
    public static string InferCandidateCountry(IReadOnlyDictionary<string, string> cfg)
    {
        var explicitCountry = Cfg(cfg, "candidate_country").Trim();
        if (explicitCountry.Length > 0) return explicitCountry;
        return BrazilHint().IsMatch(Cfg(cfg, "candidate_city")) ? "Brazil" : "";
    }

    public static List<string> AcceptedCities(IReadOnlyDictionary<string, string> cfg)
    {
        var cities = new List<string> { Core.Text.NormalizeText(Cfg(cfg, "candidate_city")) };
        cities.AddRange(Geo.ParsePreferred(Cfg(cfg, "linkedin_preferred_countries")).Select(p => Core.Text.NormalizeText(p.City)));
        return cities.Where(c => c.Length > 0).Distinct().ToList();
    }

    public static string AcceptedCityLabels(IReadOnlyDictionary<string, string> cfg)
    {
        var names = new List<string> { Cfg(cfg, "candidate_city").Trim() };
        names.AddRange(Geo.ParsePreferred(Cfg(cfg, "linkedin_preferred_countries")).Select(p => p.City));
        var seen = new HashSet<string>();
        var labels = new List<string>();
        foreach (var name in names)
        {
            var key = Core.Text.NormalizeText(name);
            if (key.Length > 0 && seen.Add(key)) labels.Add(name);
        }
        return string.Join(" / ", labels);
    }

    public static bool GeoEnabled(IReadOnlyDictionary<string, string> cfg) => (cfg.TryGetValue("geo_eligibility_enabled", out var v) ? v : "1").Trim() != "0";

    /// <summary>
    /// Exterior: bloqueia presencial/híbrido citado ou restrição citada a candidatos de fora. Presencial/híbrido no país: só na cidade
    /// de residência. Silêncio sobre estrangeiros ou sobre o modelo de trabalho não bloqueia: só uma restrição explícita.
    /// </summary>
    public static List<string> GeoBlocks(JsonObject stated, AssessJob job, IReadOnlyDictionary<string, string> cfg, string language = "")
    {
        if (!GeoEnabled(cfg)) return [];
        var cities = AcceptedCities(cfg);
        var homeCountry = CountryKey(InferCandidateCountry(cfg));
        var jobCountry = CountryKey(S(stated["job_country"]));
        var workplace = Core.Text.Truthy(stated["workplace"]) ? S(stated["workplace"]) : "unspecified";
        var eligibility = Core.Text.Truthy(stated["foreign_eligibility"]) ? S(stated["foreign_eligibility"]) : "unstated";
        var where = Core.Text.NormalizeText($"{job.Location} {S(stated["job_city"])}");

        if (eligibility == "restricted") return ["vaga restrita a candidatos de locais que excluem o candidato"];
        bool foreign;
        if (jobCountry.Length > 0 && homeCountry.Length > 0) foreign = jobCountry != homeCountry;
        else if (jobCountry.Length > 0) foreign = false;
        else foreign = language is not ("" or "pt"); // sem país citado: vaga em outro idioma é tratada como exterior
        if (foreign)
        {
            if (workplace is "onsite" or "hybrid" && !cities.Any(city => where.Contains(city, StringComparison.Ordinal))) return ["vaga no exterior não remota"];
            return [];
        }
        if (workplace == "remote") return [];
        if (workplace == "unspecified" && job.SearchRemote == "1") return []; // o LinkedIn já filtrou esta vaga como remota
        var cityLabel = AcceptedCityLabels(cfg);
        if (workplace == "unspecified")
        {
            var jobCity = Core.Text.NormalizeText(S(stated["job_city"]));
            if (jobCity.Length > 0 && cities.Count > 0 && !cities.Any(item => jobCity.Contains(item, StringComparison.Ordinal)))
                return [$"vaga em {S(stated["job_city"])} sem informar trabalho remoto (fora de {cityLabel})"];
            return [];
        }
        var label = workplace == "onsite" ? "presencial" : "híbrida";
        if (cities.Count == 0) return [$"vaga {label}: configure sua cidade no Perfil para avaliar"];
        if (!cities.Any(item => where.Contains(item, StringComparison.Ordinal))) return [$"vaga {label} fora de {cityLabel}"];
        return [];
    }

    /// <summary>Bloqueios + score determinístico + decisão. Não chama IA.</summary>
    public static JsonObject Decide(Verified verified, JsonObject profile, IReadOnlyDictionary<string, string> cfg, string track, AssessJob job, string language = "")
    {
        var reqs = verified.Requirements;
        var resps = verified.Responsibilities;
        var stated = verified.Stated;
        var musts = reqs.Where(r => S(r["kind"]) == "must").ToList();
        var coreMusts = musts.Where(r => S(r["realism"]) == "core").ToList();
        var inflated = reqs.Where(r => S(r["realism"]) == "inflated").ToList();
        var mostlyInflated = musts.Count > 0 && (double)musts.Count(r => S(r["realism"]) == "inflated") / musts.Count > InflatedMajority;

        List<JsonObject> mustItems;
        bool lowConfidence;
        if (mostlyInflated || coreMusts.Count == 0)
        {
            mustItems = [.. resps];
            lowConfidence = resps.Count == 0;
            if (mustItems.Count == 0)
            {
                mustItems = coreMusts.Count > 0 ? coreMusts : reqs.Where(r => S(r["kind"]) == "nice" && S(r["realism"]) == "core").ToList();
                lowConfidence = true;
            }
        }
        else { mustItems = coreMusts; lowConfidence = false; }
        var mustCov = Coverage(mustItems);
        var niceItems = reqs.Where(r => S(r["kind"]) == "nice" && S(r["realism"]) == "core").Concat(inflated.Where(r => Credit(S(r["status"])) > 0)).ToList();
        var niceCov = niceItems.Count > 0 ? Coverage(niceItems) : mustCov;

        // caminho transferível: cobertura das responsabilidades com met/transferable verificados
        var respCov = resps.Count > 0 ? resps.Sum(r => S(r["status"]) is "met" or "transferable" ? 1.0 : S(r["status"]) == "partial" ? 0.5 : 0.0) / resps.Count : mustCov;
        var hasTransferable = resps.Concat(reqs).Any(r => S(r["status"]) == "transferable");
        var transferPath = respCov >= TransferCoreCoverage && hasTransferable ? "strong" : respCov >= 0.3 ? "partial" : "none";

        var fit = verified.RoleFamilyFit;
        var effectiveTrack = track == "transferable" || fit == "different" ? "transferable" : "core";
        double fitCredit = fit == "different" ? (transferPath == "strong" ? DifferentWithStrongTransfer : 0.0) : fit == "same" ? 1.0 : 0.7;

        var score = (int)Math.Round(100 * (WeightMust * mustCov + WeightNice * niceCov + WeightFit * fitCredit), MidpointRounding.ToEven);

        var blocks = new List<string>();
        var yearsReqs = reqs.Where(r => S(r["category"]) == "experience_years").ToList();
        var minYears = stated["min_years"]!.GetValue<double>();
        var yearsApplies = minYears > 0 && (yearsReqs.Count == 0 || yearsReqs.Any(r => S(r["realism"]) == "core"));
        var totalMonths = Number((profile["derived"] as JsonObject)?["total_months"]);
        if (totalMonths == 0 && ResumeProfile.HasUndatedRoles(profile)) yearsApplies = false; // sem datas não dá para dizer que falta experiência
        var candidateYears = totalMonths / 12;
        var educationCredit = yearsReqs.Select(r => Number(r["education_credit_years"])).DefaultIfEmpty(0).Max(); // curso que a vaga aceita no lugar de experiência
        if (educationCredit > 0) candidateYears += Math.Min(educationCredit, ResumeProfile.EducationMonths(profile) / 12.0);
        if (yearsApplies && minYears > candidateYears + YearsTolerance)
            blocks.Add($"experiência exigida {General(minYears)}+ anos > {Fixed1(candidateYears)} anos comprovados");
        var haveLanguages = CandidateLanguages(profile);
        if (haveLanguages.Count > 0)
        {
            foreach (var item in Arr(stated["required_languages"]).OfType<JsonObject>())
            {
                var key = Core.Text.NormalizeText(S(item["language"]));
                key = LanguageAliases.GetValueOrDefault(key, key);
                if (!haveLanguages.Contains(key)) blocks.Add($"idioma exigido ausente no currículo: {S(item["language"])}");
            }
        }
        if (S(stated["authorization"]) == "candidate_lacks") blocks.Add("exige autorização de trabalho que o candidato não tem");
        blocks.AddRange(GeoBlocks(stated, job, cfg, language.Length > 0 ? language : job.Language));
        if (fit == "different" && transferPath == "none") blocks.Add("área diferente sem caminho transferível verificado");
        foreach (var req in coreMusts)
            if (S(req["category"]) is "degree" or "clearance" or "authorization" && S(req["status"]) == "unmet")
                blocks.Add($"requisito eliminatório não atendido: {Core.Text.Take(S(req["text"]), 80)}");

        var threshold = OptimismThreshold(Cfg(cfg, "job_selection_optimism"));
        int minScore;
        if (effectiveTrack == "transferable")
        {
            threshold = Math.Max(0.5, threshold - TransferableCovRelax);
            minScore = ParseIntOr(Cfg(cfg, "transferable_minimum_score"), 60);
            if (respCov < TransferCoreCoverage) blocks.Add($"responsabilidades centrais cobertas {Pct0(respCov)} (< {Pct0(TransferCoreCoverage)})");
        }
        else minScore = ParseIntOr(Cfg(cfg, "minimum_match_score"), 70);

        return new JsonObject
        {
            ["track"] = effectiveTrack, ["must_cov"] = mustCov, ["nice_cov"] = niceCov, ["fit_credit"] = fitCredit,
            ["score"] = Math.Max(0, Math.Min(100, score)), ["threshold"] = threshold, ["min_score"] = Math.Max(0, Math.Min(100, minScore)),
            ["blocks"] = Strings(blocks), ["transfer_path"] = transferPath, ["resp_cov"] = respCov,
            ["mostly_inflated"] = mostlyInflated, ["low_confidence"] = lowConfidence,
            ["must_ids"] = Strings(mustItems.Select(i => S(i["id"]))), ["inflated_ids"] = Strings(inflated.Select(r => S(r["id"]))),
        };
    }

    /// <summary><c>int(cfg.get(key) or default)</c>: texto inválido cai no padrão.</summary>
    private static int ParseIntOr(string raw, int fallback)
    {
        if (raw.Length == 0) return fallback;
        return int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    private static bool Approved(JsonObject decision, double penalty = 0.0)
    {
        var coverage = Math.Max(0.0, decision["must_cov"]!.GetValue<double>() - penalty);
        return ((JsonArray)decision["blocks"]!).Count == 0 && coverage >= decision["threshold"]!.GetValue<double>()
            && decision["score"]!.GetValue<int>() >= decision["min_score"]!.GetValue<int>();
    }

    public static List<JsonObject> VerifySkeptic(JsonObject raw, NormalizedText jobNorm, Verified verified)
    {
        var inflated = verified.Requirements.Where(r => S(r["realism"]) == "inflated").Select(r => S(r["id"])).ToHashSet();
        var result = new List<JsonObject>();
        foreach (var item in Arr(raw["objections"]).OfType<JsonObject>())
        {
            if (!(item["job_quote"] is JsonValue q && q.TryGetValue<string>(out var quote) && ResumeProfile.QuoteInText(quote, jobNorm))) continue;
            var kind = S(item["kind"]) is "new_issue" or "requirement_unmet" or "inflated_is_core" ? S(item["kind"]) : "new_issue";
            var severity = S(item["severity"]) is "blocker" or "major" or "minor" ? S(item["severity"]) : "minor";
            var rid = S(item["requirement_id"]);
            if (inflated.Contains(rid) && kind != "inflated_is_core" && severity == "blocker") severity = "minor"; // exigência desconsiderada não reprova sozinha
            result.Add(new JsonObject
            {
                ["kind"] = kind, ["requirement_id"] = rid, ["severity"] = severity, ["job_quote"] = Core.Text.Take(quote, 300), ["reason"] = Cut(item["reason"], 300),
            });
        }
        return result;
    }

    public static string BuildReason(Verified verified, JsonObject decision, List<JsonObject> objections, bool approved, string language)
    {
        var pt = language == "pt";
        var mustIds = Arr(decision["must_ids"]).Select(S).ToHashSet();
        var musts = verified.Requirements.Where(r => mustIds.Contains(S(r["id"]))).ToList();
        if (musts.Count == 0) musts = verified.Responsibilities.Where(r => mustIds.Contains(S(r["id"]))).ToList();
        var ok = musts.Count(r => S(r["status"]) is "met" or "transferable");
        var transferable = musts.Count(r => S(r["status"]) == "transferable");
        var missing = musts.Where(r => S(r["status"]) is "unmet" or "unknown").Select(r => Core.Text.Take(S(r["text"]), 60)).Take(4).ToList();
        var parts = new List<string>
        {
            (pt ? $"Obrigatórios {ok}/{musts.Count} atendidos" : $"Required {ok}/{musts.Count} met")
            + (pt && transferable > 0 ? $" ({transferable} transferível/is)" : transferable > 0 ? $" ({transferable} transferable)" : ""),
        };
        if (missing.Count > 0) parts.Add((pt ? "faltam: " : "missing: ") + string.Join("; ", missing));
        var ignored = verified.Requirements.Where(r => S(r["realism"]) == "inflated").ToList();
        if (ignored.Count > 0)
            parts.Add((pt ? "ignorados (inflados): " : "disregarded (inflated): ")
                + string.Join("; ", ignored.Take(3).Select(r => $"{Core.Text.Take(S(r["text"]), 50)} — {Core.Text.Take(S(r["realism_reason"]), 70)}")));
        var blocks = Arr(decision["blocks"]).Select(S).ToList();
        if (blocks.Count > 0) parts.Add((pt ? "bloqueios: " : "blockers: ") + string.Join("; ", blocks));
        var big = objections.Where(o => S(o["severity"]) is "blocker" or "major").ToList();
        if (big.Count > 0)
            parts.Add((pt ? "revisão cética: " : "skeptic review: ")
                + string.Join("; ", big.Take(3).Select(o => Core.Text.Take(S(o["reason"]), 100) is { Length: > 0 } r ? r : Core.Text.Take(S(o["job_quote"]), 60))));
        parts.Add($"score {decision["score"]!.GetValue<int>()}/100 · cobertura {Pct0(decision["must_cov"]!.GetValue<double>())} (mín. {Pct0(decision["threshold"]!.GetValue<double>())}) · "
            + (pt ? "trilha " : "track ") + S(decision["track"]));
        return Core.Text.Take(string.Join(". ", parts), 1000);
    }

    // ------------------------------------------------------------ orquestração

    public sealed record Outcome(int MatchScore, bool ShouldApply, bool LetterRequired, string Reason, JsonObject Assessment, IReadOnlyList<string> Bridges, string Track);

    /// <summary>Passo A (auditoria) → decisão determinística → passo B cético (só se aprovada).</summary>
    public static async Task<Outcome> AssessAsync(AssessJob job, IReadOnlyDictionary<string, string> cfg, JsonObject profile, NormalizedText resumeNorm,
        CallJson callJson, string language, string preferences, string track = "core", Action<string>? progress = null, CancellationToken ct = default)
    {
        var say = progress ?? (_ => { });
        var jobText = Core.Text.CleanJobHtml(job.Description);
        var jobNorm = NormalizedText.Of($"{job.Title}\n{job.Company}\n{job.Location}\n{jobText}");
        var raw = await callJson(BuildAssessPrompt(job, jobText, profile, cfg, language, preferences), AssessSchema(), 8192, 1024, ct);
        var verified = Verify(raw, jobNorm, resumeNorm, profile);
        var decision = Decide(verified, profile, cfg, track, job, language);
        var approved = Approved(decision);
        List<JsonObject> MustItems() =>
            verified.Requirements.Concat(verified.Responsibilities).Where(r => Arr(decision["must_ids"]).Select(S).Contains(S(r["id"]))).ToList();
        var musts = MustItems();
        say($"Passo A (auditoria): {musts.Count(r => S(r["status"]) is "met" or "transferable")}/{musts.Count} obrigatórios atendidos · cobertura {Pct0(decision["must_cov"]!.GetValue<double>())} "
            + $"(mín. {Pct0(decision["threshold"]!.GetValue<double>())}) · score {decision["score"]!.GetValue<int>()}/100 (mín. {decision["min_score"]!.GetValue<int>()})");
        var blocks = Arr(decision["blocks"]).Select(S).ToList();
        if (blocks.Count > 0) say("Bloqueios: " + string.Join("; ", blocks));
        var inflated = verified.Requirements.Where(r => S(r["realism"]) == "inflated").Select(r => Core.Text.Take(S(r["text"]), 60)).ToList();
        if (inflated.Count > 0) say($"Requisitos desconsiderados (inflados): {string.Join("; ", inflated.Take(4))}");
        var objections = new List<JsonObject>();
        if (!approved) say("Passo B (revisão cética) pulado: reprovada no passo A");
        if (approved)
        {
            say("Passo A aprovou; rodando revisão cética (passo B)");
            var skeptic = await callJson(BuildSkepticPrompt(jobText, profile, verified), SkepticSchema(), 4096, 1024, ct);
            objections = VerifySkeptic(skeptic, jobNorm, verified);
            var flipped = false;
            foreach (var objection in objections.Where(o => S(o["kind"]) == "inflated_is_core"))
                foreach (var req in verified.Requirements.Where(r => S(r["id"]) == S(objection["requirement_id"]) && S(r["realism"]) == "inflated"))
                {
                    req["realism"] = "core";
                    req["realism_reason"] = "";
                    flipped = true;
                }
            if (flipped) decision = Decide(verified, profile, cfg, track, job, language);
            var majors = objections.Count(o => S(o["severity"]) == "major");
            var blockers = objections.Count(o => S(o["severity"]) == "blocker");
            var penalty = Math.Min(MajorObjectionCap, MajorObjectionPenalty * majors);
            if (blockers > 0) ((JsonArray)decision["blocks"]!).Add("objeção bloqueante verificada na revisão cética");
            approved = Approved(decision, penalty);
            say($"Passo B (revisão cética): {objections.Count} objeção(ões) verificada(s) ({blockers} bloqueante(s), {majors} grave(s)) · resultado final: {(approved ? "aprovada" : "reprovada")}");
        }
        var bridges = verified.Requirements.Concat(verified.Responsibilities)
            .Where(r => S(r["status"]) == "transferable" && Core.Text.Truthy(r["bridge"]))
            .Select(r => $"{Core.Text.Take(S(r["text"]), 120)} ← {S(r["bridge"])}").Take(8).ToList();
        var assessment = new JsonObject
        {
            ["version"] = ScoringVersion, ["track"] = decision["track"]!.DeepClone(), ["decision"] = decision,
            ["requirements"] = new JsonArray(verified.Requirements.Select(r => (JsonNode?)r).ToArray()),
            ["responsibilities"] = new JsonArray(verified.Responsibilities.Select(r => (JsonNode?)r).ToArray()),
            ["level_label"] = verified.LevelLabel, ["stated"] = verified.Stated, ["role_family_fit"] = verified.RoleFamilyFit,
            ["objections"] = new JsonArray(objections.Select(o => (JsonNode?)o).ToArray()), ["dropped"] = verified.Dropped, ["bridges"] = Strings(bridges),
            ["inflated_posting"] = decision["mostly_inflated"]!.DeepClone(),
        };
        return new Outcome(decision["score"]!.GetValue<int>(), approved, verified.CoverLetterRequired, BuildReason(verified, decision, objections, approved, language),
            assessment, bridges, S(decision["track"]));
    }
}
