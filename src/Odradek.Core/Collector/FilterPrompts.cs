namespace Odradek.Core.Collector;

/// <summary>Prompts padrão dos filtros do LinkedIn, copiados do <c>app.py</c> (a geração automática lê estes textos; o painel só guarda o que difere deles).</summary>
public static class FilterPrompts
{
    public const string DefaultMain = """
You design the ONE main LinkedIn Jobs search filter for this candidate (Apify, curious_coder linkedin-jobs-scraper).
The main area is IT and programming: the area where the candidate has the most verified experience, the one that defines their career. Return EXACTLY ONE filter, as sharp as possible: the single job title that best captures what is most career-defining for this candidate, grounded ONLY in the verified profile below.

LinkedIn experience codes (f_E): 1 Internship, 2 Entry, 3 Associate, 4 Mid-Senior, 5 Director, 6 Executive.

KEYWORD RULES (mandatory):
- `keywords` holds 1 or 2 phrases that all name the SAME role: the title plus at most ONE equivalent variant of that same title (for example its Portuguese/English version or its most common synonym). Never a second, different role and never an intersection of unrelated skills.
- NO PROGRAMMING LANGUAGE, FRAMEWORK OR TOOL in any phrase (no "C#", "Python", "Java", "ASP.NET Core", "React", "SQL", "AWS"...). With AI-assisted development, the specific language matters little for this candidate; what defines the career is logical reasoning, problem solving and knowledge of software architecture (system design, APIs, data modeling, integrations, automation). Search by stack-agnostic role title ("Software Engineer", "Software Developer", "Desenvolvedor de Software", "Backend Engineer", "Full Stack Developer"...). Phrases that name a language, framework or tool found in the profile are discarded by the program.
- Choose the role family from what the dated roles show the candidate DOES (building, designing and automating software and integrations), not from the language used. A role whose title is not a developer title but whose responsibilities are software development or automation can anchor the filter; explain it in `reason`.
- EVERY phrase must list `evidence_ids`, and at least one must be a ROLE id ([R1]) or one of its responsibilities ([R1.2]). Phrases that do not are discarded by the program. Prefer responsibilities that show reasoning, architecture, integrations or automation.
- `anchor_role_ids`: the role ids that prove this career line.
- Do NOT invent employers, titles or skills. No fixed seniority-to-code mapping and no universal years cutoff: choose `experience_levels` from the computed months of the anchor roles and explain the level and the evidence in `reason` (Portuguese).
- Location and work model are NOT yours to choose (the program derives them); do not mention them.

Panel keyword preferences (roles/skills context only): {pref_keywords}
Candidate facts PT: {facts_pt}
Candidate facts EN: {facts_en}

VERIFIED CANDIDATE PROFILE (PT and EN resumes merged; evidence ids in brackets):
{profile}

""";

    public const string DefaultSecondary = """
You design the ONE secondary (backup) LinkedIn Jobs search filter for this candidate.
Goal: ONE role, in a sector and function that are historically LESS exposed to economic downturns, where this candidate's VERIFIED capabilities transfer, especially the combination of IT/programming skills with administrative/operational experience.

LinkedIn experience codes (f_E): 1 Internship, 2 Entry, 3 Associate, 4 Mid-Senior, 5 Director, 6 Executive.

How to choose (use the profile's TRANSFERABLE CAPABILITIES, roles and skills as evidence):
- Think function x sector. Functions where IT plus an administrative background is a differentiator: IT support/helpdesk, systems/ERP-oriented administrative analyst, process/automation analyst, data/BI assistant, back-office with systems, implementation or support of business software. Sectors that tend to hold up in downturns: healthcare, public sector and outsourced public services, education, utilities/energy, logistics/supply chain, insurance, accounting/tax, back-office of financial services. These lists are only illustrative: choose what THIS profile evidences and justify the choice.
- The candidate's hint for the backup area (a direction, not an order; ignore it if the profile gives no evidence for it): {area_hint}
- The MAIN filter already covers: {main_filter}. Choose a DIFFERENT role family; do not repeat its titles.
- `keywords` holds 1 or 2 phrases that all name the SAME role (the title plus at most one equivalent variant), optionally with a sector qualifier. Never a bare skill, and never a programming language, framework or tool name (phrases that name one found in the profile are discarded).
- EVERY phrase must list `evidence_ids`, and at least one must be a TRANSFERABLE capability ([T1]) or a role/responsibility ([R1], [R1.2]). Phrases without valid ids are discarded.
- `anchor_role_ids`: the roles whose experience transfers.
- Fill `sector` (the target sector) and `resilience_reason` (why this sector/function is comparatively resilient AND which profile evidence transfers). Stay qualitative; no statistics you cannot support.
- Choose experience codes realistic for moving into a new function (often Entry/Associate), justified by the profile. Explain in `reason` (Portuguese).
- Location and work model are NOT yours to choose; do not mention them.

Panel keyword preferences (context only): {pref_keywords}
Candidate facts PT: {facts_pt}
Candidate facts EN: {facts_en}

VERIFIED CANDIDATE PROFILE (PT and EN resumes merged; evidence ids in brackets):
{profile}

""";
}
