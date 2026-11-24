namespace Odradek.Core.Collector;

/// <summary>Prompts padrão dos filtros do LinkedIn (a geração automática lê estes textos; o painel só guarda o que difere deles). Reescritos para o app nativo: o contrato com o código (marcadores <c>{profile}</c>, <c>{pref_keywords}</c>, <c>{facts_pt}</c>, <c>{facts_en}</c>, <c>{area_hint}</c>, <c>{main_filter}</c>; ids de evidência; 1–2 frases do mesmo cargo; sem tecnologia nas frases) é o mesmo de antes e está coberto por testes.</summary>
public static class FilterPrompts
{
    public const string DefaultMain = """
You design the ONE main LinkedIn Jobs search filter for this candidate (Apify linkedin-jobs-scraper). Work ONLY from the verified profile below: every choice must trace back to an evidence id.

GOAL
The main area is IT and software. Pick the single job title that best captures the candidate's most career-defining, best-evidenced work, written the way recruiters title it on LinkedIn. Sharp beats broad: one role family, never a bundle of skills.

HOW TO CHOOSE THE ROLE
1. Read the dated roles and their responsibilities. Weigh what the person DID (building, designing, integrating and automating software), for how long (computed months) and how recently, not which language they used.
2. A role whose title is not a developer title can anchor the filter when its responsibilities are software development or automation; say so in `reason`.
3. Prefer evidence of reasoning, architecture, integrations and automation over routine maintenance or support tasks.
4. If the evidence is thin, choose the best-supported title instead of stretching to a more impressive one.

KEYWORDS (`keywords`: 1 or 2 objects {phrase, evidence_ids}) - mandatory
- Both phrases name the SAME role: the title plus at most ONE equivalent variant (for example its Portuguese or English version, or its most common synonym). Never a second, different role and never an intersection of skills.
- Plain job-title text, 2 to 4 words: no boolean operators (AND, OR, NOT), quotes, parentheses or commas.
- NO programming language, framework, tool, cloud or database name ("C#", "Python", "Java", "React", "SQL", "AWS"...). With AI-assisted development the stack matters little for this candidate; the career is defined by logical reasoning, problem solving and software architecture (system design, APIs, data modeling, integrations, automation). Use stack-agnostic titles such as "Software Engineer", "Software Developer", "Desenvolvedor de Software", "Backend Engineer", "Full Stack Developer". The program discards any phrase that names a technology found in the profile.
- NO seniority words (Junior, Senior, Pleno, Sênior, Lead, Jr, Sr, Estagiário): seniority goes in `experience_levels`, and putting it in the phrase only shrinks the results.
- Use titles that real employers post. Do not invent an unusual title to look unique.
- EVERY phrase lists `evidence_ids`, and at least one must be a ROLE id ([R1]) or one of its responsibilities ([R1.2]). The program discards phrases without a valid id.

OTHER FIELDS
- `name`: short label for the filter, in Portuguese (2 to 4 words).
- `anchor_role_ids`: the role ids that prove this career line (the ones you cited).
- `experience_levels`: LinkedIn f_E codes (1 Internship, 2 Entry, 3 Associate, 4 Mid-Senior, 5 Director, 6 Executive). Give 1 or 2 ADJACENT codes, derived from the computed months of the anchor roles (no fixed years-to-level table, no universal cutoff). When unsure, include the lower neighbour rather than reaching up.
- `reason`: Portuguese, at most 350 characters: why this title, which roles or responsibilities support it, and why these levels.
- `sector` and `resilience_reason`: empty strings.

NEVER
- Invent employers, titles, skills or dates.
- Mention location, remote/hybrid or work model: the program derives them.

Panel keyword preferences (context on roles and skills only, never a source of evidence): {pref_keywords}
Candidate facts PT: {facts_pt}
Candidate facts EN: {facts_en}

VERIFIED CANDIDATE PROFILE (PT and EN resumes merged; evidence ids in brackets):
{profile}
""";

    public const string DefaultSecondary = """
You design the ONE secondary (backup) LinkedIn Jobs search filter for this candidate (Apify linkedin-jobs-scraper). Work ONLY from the verified profile below: every choice must trace back to an evidence id.

GOAL
One role, in a function and sector that are historically LESS exposed to economic downturns, where this candidate's VERIFIED capabilities transfer, especially IT/programming skills combined with administrative or operational experience. It is a safety net, so it must be realistic to get hired in, not aspirational.

HOW TO CHOOSE
- Think function x sector, and use the profile's TRANSFERABLE CAPABILITIES ([T1]...), roles and skills as the evidence.
- Functions where IT plus an administrative background is a differentiator: IT support/helpdesk, systems or ERP-oriented administrative analyst, process/automation analyst, data/BI assistant, back-office with systems, implementation or support of business software.
- Sectors that tend to hold up in downturns: healthcare, public sector and outsourced public services, education, utilities/energy, logistics/supply chain, insurance, accounting/tax, back-office of financial services.
- Both lists are only illustrative: choose what THIS profile evidences and justify it.
- The candidate's hint for the backup area (a direction, not an order; ignore it when the profile gives no evidence for it): {area_hint}
- The MAIN filter already covers: {main_filter}. Choose a DIFFERENT role family and do not repeat its titles or close synonyms.

KEYWORDS (`keywords`: 1 or 2 objects {phrase, evidence_ids}) - mandatory
- Both phrases name the SAME role: the title plus at most ONE equivalent variant (Portuguese or English version, or most common synonym). Never a second, different role.
- Plain job-title text, 2 to 4 words, no boolean operators, quotes, parentheses or commas. Add a sector word only when real postings title the job that way; otherwise keep the sector in `sector`.
- Never a bare skill and never a programming language, framework or tool name (the program discards phrases that name one found in the profile).
- NO seniority words (Junior, Senior, Pleno, Lead, Estagiário...): seniority goes in `experience_levels`.
- EVERY phrase lists `evidence_ids`, and at least one must be a TRANSFERABLE capability ([T1]) or a role/responsibility ([R1], [R1.2]). The program discards phrases without a valid id.

OTHER FIELDS
- `name`: short label for the filter, in Portuguese (2 to 4 words).
- `anchor_role_ids`: the roles whose experience transfers.
- `sector`: the target sector, in Portuguese.
- `resilience_reason`: Portuguese, one or two sentences: why this function/sector is comparatively resilient AND which profile evidence transfers. Stay qualitative: no statistics, rankings or figures you cannot support.
- `experience_levels`: LinkedIn f_E codes (1 Internship, 2 Entry, 3 Associate, 4 Mid-Senior, 5 Director, 6 Executive). Give 1 or 2 ADJACENT codes that are realistic for moving into a new function (often Entry and Associate), justified by the profile.
- `reason`: Portuguese, at most 350 characters: why this role fits and why these levels.

NEVER
- Invent employers, titles, skills or dates.
- Mention location, remote/hybrid or work model: the program derives them.

Panel keyword preferences (context only, never a source of evidence): {pref_keywords}
Candidate facts PT: {facts_pt}
Candidate facts EN: {facts_en}

VERIFIED CANDIDATE PROFILE (PT and EN resumes merged; evidence ids in brackets):
{profile}
""";
}
