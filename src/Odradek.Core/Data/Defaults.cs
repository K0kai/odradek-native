namespace Odradek.Core.Data;

public sealed record FormRuleRow(string Key, string Aliases, string Mode, string ValueFrom, string Value, int SortOrder);

/// <summary>Valores padrão: cópia de <c>DEFAULT_SETTINGS</c> (<c>repository.py:64</c>) e de <c>DEFAULT_RULES</c> (<c>form_rules.py:11</c>).</summary>
public static class Defaults
{
    public static readonly (string Key, string Value)[] Settings =
    [
        ("keywords", "software engineer, desenvolvedor, developer, backend, frontend, full stack, data engineer, devops"),
        ("locations", "remote, remoto, brazil, brasil, worldwide, anywhere"),
        ("sources", "remotive,remoteok"),
        ("ai_provider", "gemini"),
        ("ai_model", "gemini-3.5-flash-lite"),
        // Último recurso quando todos os modelos do provedor falham; só vale com a chave do TokenHarbor salva.
        ("tokenharbor_fallback_models", "deepseek-v4.1-flash:free,mimo-v2.6-flash:free"),
        ("candidate_name", ""), ("candidate_email", ""), ("candidate_phone", ""), ("candidate_linkedin", ""),
        ("candidate_city", ""), ("candidate_street", ""), ("candidate_postal_code", ""), ("candidate_state", ""),
        ("candidate_country", ""), ("candidate_current_company", ""), ("candidate_cpf", ""),
        // Expectativa salarial: o robô escolhe BRL x USD pela moeda do campo/vaga
        ("salary_expectation_brl", ""), ("salary_expectation_usd", ""),
        ("salary_currency_preference", "auto"), ("salary_pj_multiplier", "1.3"),
        // Preferência de contratação (radio Contractor/Employee); 'ask' = deixar com você
        ("candidate_contract_type", "employee"),
        ("candidate_profile_pt", ""), ("candidate_profile_en", ""), ("candidate_facts_pt", ""), ("candidate_facts_en", ""),
        ("resume_pt_path", ""), ("resume_en_path", ""),
        ("auto_apply", "0"), ("browser_engine", "pydoll"),
        ("minimum_match_score", "70"), ("job_selection_optimism", "75"),
        // Dois filtros: Principal (TI/programação) e Secundário (área de reserva resiliente). Cada slot guarda UM objeto JSON (ou vazio).
        ("linkedin_main_filter_json", ""), ("linkedin_secondary_filter_json", ""),
        ("linkedin_main_share", "70"), ("linkedin_max_age_days", "7"), ("linkedin_preferred_countries", ""),
        ("secondary_area_hint", ""), ("geo_eligibility_enabled", "1"),
        // Portfólio público (schema 2 de /ai/profile.json) como segunda fonte do perfil; vazio = desligado.
        ("portfolio_profile_url", ""), ("portfolio_version", ""), ("portfolio_profile_json", ""),
        ("portfolio_text", ""), ("portfolio_synced_at", ""), ("portfolio_error", ""),
        ("transferable_minimum_score", "60"), ("transferable_prompt", ""), ("job_selection_prompt", ""),
        ("maximum_applications_per_run", "5"), ("adzuna_countries", "br,us,gb,ca"),
        ("apify_monthly_credit_limit_usd", "5"), ("apify_job_count", "25"),
        ("apify_actors_json", """[{"id": "curious_coder~linkedin-jobs-scraper", "label": "LinkedIn Jobs", "enabled": true, "input_mode": "linkedin_search", "count": 25}]"""),
        // Vazio = prompt padrão (editável em Busca)
        ("linkedin_filter_prompt", ""), ("ai_usage_json", ""), ("apify_quota_snapshot_json", ""),
        ("smtp_host", ""), ("smtp_port", "587"), ("smtp_user", ""), ("smtp_from", ""), ("smtp_use_tls", "1"),
        ("queue_max_workers", "3"), ("queue_max_attempts", "40"), ("queue_ttl_hours", "24"),
        ("worth_reverify_last_daily", ""), ("standby_sync_last_daily", ""), ("standby_last_sync_json", ""),
        // Perfil de diversidade (identidade NUNCA vem de IA; 'not_informed' = não informar)
        ("candidate_gender", "not_informed"), ("candidate_race", "not_informed"),
        ("candidate_lgbtq", "not_informed"), ("candidate_pcd", "no"), ("candidate_diversity_note", ""),
    ];

    public static readonly FormRuleRow[] FormRules =
    [
        new("full_name", "name,full_name,fullname,nome completo,your name,candidate name", "text", "candidate_name", "", 10),
        new("email", "email,e-mail,mail,correo", "text", "candidate_email", "", 20),
        new("phone", "phone,telephone,tel,telefone,celular,mobile,whatsapp", "text", "candidate_phone", "", 30),
        new("linkedin", "linkedin,linkedin url,profile url", "text", "candidate_linkedin", "", 40),
        new("city", "city,cidade,cidade atual,current city,localidade", "text", "candidate_city", "", 50),
        new("street_address", "street address,address line,endereço,endereco,rua,logradouro,street name,nome da rua,address 1,address line 1", "text", "candidate_street", "", 50),
        new("postal_code", "zip,postal code,cep,codigo postal,código postal,postcode,zip code,código cep", "text", "candidate_postal_code", "", 51),
        new("state", "state,estado,province,provincia,província,uf,region,estado/provincia", "text", "candidate_state", "", 52),
        new("country", "country of origin,país de origem,pais de origem,country,país,pais,nationality country,select a country", "select", "candidate_country", "", 53),
        new("current_company", "current company,empresa atual,current employer,most recent employer,employer name,nome da empresa,company you work,where do you currently work,currently work", "text", "candidate_current_company", "", 54),
        new("cpf", "cpf,cadastro de pessoa física,documento,tax id,tax identification,id number,cpf number,número do documento", "text", "candidate_cpf", "", 56),
        new("salary", "salary,compensation,expected salary,salary expectation,faixa salarial,pretensão,pretensão salarial,remuneração,pay,salario,expectativa salarial", "salary", "", "", 60),
        new("contract_type", "contract type,type of contract,employment type,tipo de contratacao,contratacao,modelo de contratacao,regime", "select", "candidate_contract_type", "", 65),
        new("cover_letter", "cover letter,carta,carta de apresentação,message,mensagem,additional information,comments", "cover_letter", "", "", 70),
        new("resume_file", "resume,cv,curriculum,currículo,upload resume,anexar currículo,attach resume", "file", "", "", 80),
    ];

    /// <summary>Rótulos de status (<c>STATUSES</c> em <c>app.py:49</c>); a ordem importa para os seletores.</summary>
    public static readonly (string Key, string Label)[] Statuses =
    [
        ("new", "Nova"), ("review", "Na fila"), ("worth", "Vale a pena olhar"), ("saved", "Salva"),
        ("prepared", "Carta preparada"), ("applied", "Aplicada"), ("ignored", "Ignorada"), ("blocked", "Envio indisponível"),
    ];

    public static bool IsStatus(string? status) => Statuses.Any(s => s.Key == status);
}
