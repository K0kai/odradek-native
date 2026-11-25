using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Candidate;
using Odradek.Core.Data;

namespace Odradek.Core.Engines;

/// <summary>Assistente do painel: pergunta de formulário → resposta pronta para colar (<c>candidate/assistant_chat.py</c>, <c>candidate_context.py</c>).</summary>
public sealed class Assistant(Database db, AiClient ai, Func<string, string>? secret = null)
{
    public const int MaxQuestionChars = 4000;
    public const int MaxHistoryTurns = 8;
    public const int MaxAnswerChars = 2000;
    public const int ResumeBudget = 6000;

    public sealed record Turn(string Role, string Content);

    private static readonly (string Key, string Label)[] PanelProfileFields =
    [
        ("candidate_name", "name"), ("candidate_email", "email"), ("candidate_phone", "phone"), ("candidate_linkedin", "linkedin"),
        ("candidate_city", "city"), ("candidate_street", "street"), ("candidate_postal_code", "postal_code"), ("candidate_state", "state"),
        ("candidate_country", "country"), ("candidate_current_company", "current_company"), ("candidate_cpf", "cpf"),
        ("salary_expectation_brl", "salary_brl"), ("salary_expectation_usd", "salary_usd"), ("candidate_contract_type", "contract_type"),
    ];

    private static string Cfg(IReadOnlyDictionary<string, string>? cfg, string key) => cfg is not null && cfg.TryGetValue(key, out var v) ? v.Trim() : "";

    /// <summary>Valores não vazios do perfil do painel para os prompts do assistente.</summary>
    public static string PanelProfileBlock(IReadOnlyDictionary<string, string>? cfg)
    {
        var lines = new List<string>();
        foreach (var (key, label) in PanelProfileFields)
        {
            var value = Cfg(cfg, key);
            if (value.Length > 0) lines.Add($"{label}: {value}");
        }
        if (Cfg(cfg, "salary_expectation_brl").Length > 0 || Cfg(cfg, "salary_expectation_usd").Length > 0)
            lines.Add("salary_note: panel amounts are MONTHLY mid-level targets; convert FX; "
                + "match field period; for intern/junior LOWER unrealistic figures "
                + "(may go below panel); for mid+ stay above Brazil floor and slightly "
                + "under local market; prefer salary_usd when present");
        return string.Join('\n', lines);
    }

    private static string DiversityBlock(IReadOnlyDictionary<string, string> cfg)
    {
        var lines = new List<string>();
        foreach (var (key, label) in new[]
        {
            ("candidate_gender", "gender"), ("candidate_race", "race_ethnicity"), ("candidate_pcd", "pcd_disability"),
            ("candidate_lgbtq", "lgbtq"), ("candidate_diversity_note", "diversity_note"),
        })
        {
            var value = Cfg(cfg, key);
            if (value.Length > 0) lines.Add($"{label}: {value}");
        }
        return string.Join('\n', lines);
    }

    private static string ResumeDossier((string Summary, string AnalysisJson)? row, string language)
    {
        if (row is null) return "";
        var summary = row.Value.Summary.Trim();
        var raw = row.Value.AnalysisJson.Trim();
        if (raw.Length > 0)
        {
            try
            {
                if (JsonNode.Parse(raw) is JsonObject { Count: > 0 } data)
                {
                    var dossier = ResumeAnalysis.ComposeDossier(data, language);
                    if (dossier.Length > 0) return Text.Take(dossier, ResumeBudget);
                }
            }
            catch (JsonException) { }
        }
        return Text.Take(summary, ResumeBudget);
    }

    /// <summary>Contexto do prompt: perfil do painel + fatos + dossiê dos currículos.</summary>
    public static string BuildContext(IReadOnlyDictionary<string, string> cfg, Func<string, (string Summary, string AnalysisJson)?> resume)
    {
        var parts = new List<string>();
        var profile = PanelProfileBlock(cfg);
        if (profile.Length > 0) parts.Add("Panel profile:\n" + profile);
        var diversity = DiversityBlock(cfg);
        if (diversity.Length > 0) parts.Add("Diversity preferences:\n" + diversity);
        var factsPt = Cfg(cfg, "candidate_facts_pt");
        var factsEn = Cfg(cfg, "candidate_facts_en");
        if (factsPt.Length > 0) parts.Add("Candidate facts (PT):\n" + Text.Take(factsPt, 3000));
        if (factsEn.Length > 0) parts.Add("Candidate facts (EN):\n" + Text.Take(factsEn, 3000));
        foreach (var (lang, title) in new[] { ("pt", "Resume dossier (PT)"), ("en", "Resume dossier (EN)") })
        {
            var dossier = ResumeDossier(resume(lang), lang);
            if (dossier.Length > 0) parts.Add($"{title}:\n{dossier}");
        }
        var context = string.Join("\n\n", parts).Trim();
        return context.Length > 0 ? context : "[no profile or resume data available]";
    }

    /// <summary>Histórico do chat: aceita lista ou JSON em texto; só turnos user/assistant com conteúdo, os últimos 16.</summary>
    public static List<Turn> NormalizeHistory(JsonNode? raw)
    {
        if (raw is JsonValue value && value.TryGetValue<string>(out var text))
        {
            text = text.Trim();
            if (text.Length == 0) return [];
            try { raw = JsonNode.Parse(text); }
            catch (JsonException) { return []; }
        }
        if (raw is not JsonArray items) return [];
        var output = new List<Turn>();
        foreach (var item in items.Skip(Math.Max(0, items.Count - MaxHistoryTurns * 2)))
        {
            if (item is not JsonObject turn) continue;
            var role = Text.PyStr(turn["role"]).Trim().ToLowerInvariant();
            var content = Text.PyStr(turn["content"]).Trim();
            if (role is not ("user" or "assistant") || content.Length == 0) continue;
            output.Add(new Turn(role, Text.Take(content, MaxQuestionChars)));
        }
        return output;
    }

    public static string BuildPrompt(string question, string context, IReadOnlyList<Turn>? history = null)
    {
        var historyBlock = "";
        if (history is { Count: > 0 })
            historyBlock = "Prior turns in this chat:\n" + string.Join("\n", history.Select(t => $"{(t.Role == "user" ? "User" : "Assistant")}: {t.Content}")) + "\n\n";
        return $"""
You help a job applicant fill application form fields.
Reply with ONLY the value they should paste into the form (or a short ready-to-paste answer).
Match the language of the question (Portuguese or English).
For anything about the candidate, use ONLY the candidate context below. Never invent employers, projects, metrics, skills, addresses, or identity facts.

First decide what kind of question this is:
A) A question about the candidate (personal data, history, experience, availability, preferences). If the context is insufficient, reply with a single short sentence starting with "FALTA:" (or "MISSING:" if the question is in English) naming exactly what is missing — do not invent a guess.
B) A hypothetical, technical, or case question that asks how the candidate WOULD solve, design, verify, debug, prioritize, or decide something, or what they know or think about a technical topic. It tests reasoning, so answer it:
- Use sound general professional knowledge, in the first person ("Eu verificaria…" / "I would check…"). It does not need to be in the candidate context.
- Not knowing the company's or product's internals is NEVER a reason to reply FALTA or MISSING. Do not claim to know them: state a brief assumption, or say what you would confirm first, and still give the approach.
- Answer every part of the question. Pick the few checks that matter most, make them concrete, and say how you would know they worked. Depth on those beats a complete checklist.
- Mention the candidate's own experience only when the context supports it. A proposed approach must never be presented as something the candidate has already done.
- Usually 500 to 1200 characters.
If one question mixes both kinds (for example "Have you done X? How would you do it?"), answer the approach, and state experience only as far as the context supports it.

Voice (for every answer written in sentences; plain values such as a name, date or number stay as they are):
It must read as if the candidate wrote it, explaining their thinking to a recruiter, not as an AI assistant's report.
- Natural, everyday professional language (Brazilian Portuguese or English), first person, in short flowing paragraphs. No numbered or bulleted lists, headings, bold, or "Topic: explanation" lines.
- Open straight with what you would do or what you think. Do not restate or praise the question.
- Connect the steps by reasoning ("primeiro eu olharia… porque…", "if that checks out, then…"), saying why something worries you or what could go wrong in practice.
- Vary sentence length. Prefer plain verbs and concrete examples to buzzwords and formal noun phrases.
- Avoid stock AI phrasing: "é importante ressaltar", "vale destacar", "além disso" / "por fim" chains, "garantir robustez", "de forma eficaz", "leverage", "robust", "seamless", "it's important to note", groups of three by habit, em dashes, and a closing line that sums everything up.
- Sound human through clarity and judgment only: never invent anecdotes, feelings, or past situations, and do not add slang or deliberate mistakes.

Do not wrap the answer in quotes unless the form value itself needs quotes.
Do not add markdown, preamble, or comments about the answer itself.

{TriageService.ExperiencePriorityRule}

Candidate context:
{context}

{historyBlock}Form question / label:
{question}

""";
    }

    /// <summary>Gera a resposta pronta para colar. Lança <see cref="ActionException"/> (validação) ou <see cref="AiUnavailableException"/>.</summary>
    public async Task<string> AnswerAsync(string message, JsonNode? history = null, CancellationToken ct = default)
    {
        var question = (message ?? "").Trim();
        if (question.Length == 0) throw new ActionException("Cole a pergunta do formulário.");
        if (Validate.Length(question) > MaxQuestionChars) throw new ActionException($"Pergunta muito longa (máx. {MaxQuestionChars} caracteres).");
        var cfg = new SettingsStore(db).All();
        var provider = (cfg.GetValueOrDefault("ai_provider") is { Length: > 0 } p ? p : "gemini").Trim();
        var model = (cfg.GetValueOrDefault("ai_model") is { Length: > 0 } m ? m : "gemini-3.5-flash-lite").Trim();
        var apiKey = (secret ?? (name => SecretStore.Get(name)))(provider);
        if (apiKey.Length == 0) throw new AiUnavailableException("IA indisponível: chave não configurada.");

        Dictionary<string, (string Summary, string AnalysisJson)> resumes;
        using (var connection = db.Open())
            resumes = connection.Query<(string Language, string? Summary, string? Json)>("SELECT language, analysis_summary AS Summary, analysis_json AS Json FROM resumes")
                .ToDictionary(r => r.Language, r => (r.Summary ?? "", r.Json ?? ""));
        var context = BuildContext(cfg, lang => resumes.TryGetValue(lang, out var row) ? row : null);
        var prompt = BuildPrompt(Text.Take(question, MaxQuestionChars), context, NormalizeHistory(history));
        var answer = (await ai.TextAsync(prompt, new AiCall(provider, model, apiKey), maxOutputTokens: 800, temperature: 0.2, timeoutSeconds: 90, ct: ct) ?? "").Trim();
        if (answer.Length == 0) throw new ActionException("A IA não retornou resposta.");
        new EventLogStore(db).Add("info", "assistant", "Resposta gerada no Assistente.");
        return Text.Take(answer, MaxAnswerChars);
    }
}
