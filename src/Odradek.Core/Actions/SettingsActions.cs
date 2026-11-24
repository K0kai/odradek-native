using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Odradek.Core.Collector;
using Odradek.Core.Data;

namespace Odradek.Core.Actions;

/// <summary>
/// Salvar Configurações com as validações de <c>web/panel_settings.py:91-199</c>. Segredos são só de escrita: vão para o Cofre do Windows
/// e nunca voltam para a tela. Uma chamada pode misturar grupos (uma aba da tela cruza vários); tudo é validado antes de gravar qualquer coisa.
/// </summary>
public sealed partial class SettingsActions(Database db)
{
    private static readonly Dictionary<string, string[]> Groups = new()
    {
        ["search"] = ["keywords", "locations", "sources", "adzuna_countries", "apify_monthly_credit_limit_usd", "apify_job_count", "apify_actors_json",
            "geo_eligibility_enabled", "linkedin_main_share", "linkedin_max_age_days", "linkedin_preferred_countries", "linkedin_filter_prompt",
            "linkedin_main_filter_json", "transferable_prompt", "secondary_area_hint", "linkedin_secondary_filter_json"],
        ["profile"] = ["candidate_name", "candidate_email", "candidate_phone", "candidate_linkedin", "candidate_city", "candidate_street",
            "candidate_postal_code", "candidate_state", "candidate_country", "candidate_current_company", "candidate_cpf", "salary_expectation_brl",
            "salary_expectation_usd", "salary_currency_preference", "salary_pj_multiplier", "candidate_contract_type", "candidate_gender",
            "candidate_race", "candidate_lgbtq", "candidate_pcd", "candidate_diversity_note", "portfolio_profile_url"],
        ["ai"] = ["ai_provider", "ai_model", "tokenharbor_fallback_models", "candidate_facts_pt", "candidate_facts_en", "job_selection_optimism", "job_selection_prompt"],
        ["automation"] = ["auto_apply", "minimum_match_score", "queue_max_workers", "queue_max_attempts", "queue_ttl_hours", "transferable_minimum_score"],
        ["smtp"] = ["smtp_host", "smtp_port", "smtp_user", "smtp_from", "smtp_use_tls"],
    };

    /// <summary>Chaves que vão para o cofre (nome do campo → nome do segredo; "api_key" depende do provedor).</summary>
    private static readonly string[] SecretKeys = ["api_key", "tokenharbor_api_key", "adzuna_app_id", "adzuna_app_key", "apify_token", "smtp_password", "backup_database_url"];

    [GeneratedRegex(@"^[\w.:/\-]+\z")] private static partial Regex ModelId();
    [GeneratedRegex(@"^[^\W\d_][^\W\d_ .'-]*(?: [^\W\d_][^\W\d_ .'-]*)*\z")] private static partial Regex CountryName();
    [GeneratedRegex(@"^[^\W\d_]+(?:[ .'-]+[^\W\d_]+)*\z")] private static partial Regex CityName();

    public static bool IsSecretKey(string key) => SecretKeys.Contains(key);

    public string Save(IReadOnlyDictionary<string, string> incoming)
    {
        var form = new Dictionary<string, string>();
        foreach (var key in Groups.Values.SelectMany(k => k))
            if (incoming.TryGetValue(key, out var value))
            {
                if (Validate.Length(value) > 30000) throw new ActionException($"Valor inválido para {key}");
                form[key] = value;
            }
        foreach (var key in SecretKeys)
            if (incoming.TryGetValue(key, out var value) && Validate.Length(value) > 8192) throw new ActionException($"Valor inválido para {key}");

        bool Has(string group) => Groups[group].Any(incoming.ContainsKey) || group switch
        {
            "ai" => new[] { "api_key", "tokenharbor_api_key", "adzuna_app_id", "adzuna_app_key", "apify_token" }.Any(incoming.ContainsKey),
            "smtp" => incoming.ContainsKey("smtp_password"),
            "automation" => incoming.ContainsKey("backup_database_url"),
            _ => false,
        };

        var notices = new List<string>();
        var secrets = new List<(string Name, string Value)>();
        string Secret(string key) => incoming.TryGetValue(key, out var v) ? v : "";

        if (Has("search")) { ValidateSearch(form); notices.Add("Preferências de busca salvas."); }
        if (Has("ai"))
        {
            if (form.TryGetValue("ai_provider", out var provider) && provider is not ("gemini" or "openai")) throw new ActionException("Provedor de IA inválido");
            if (form.TryGetValue("tokenharbor_fallback_models", out var raw))
            {
                var models = ParseModelList(raw);
                if (models.Count > 6 || models.Any(name => Validate.Length(name) > 100 || !ModelId().IsMatch(name)))
                    throw new ActionException("Modelos do TokenHarbor: use até 6 IDs separados por vírgula (ex.: deepseek-v4.1-flash:free)");
                form["tokenharbor_fallback_models"] = string.Join(", ", models);
            }
            if (form.TryGetValue("job_selection_optimism", out var optimism)) form["job_selection_optimism"] = NormalizeOptimism(optimism).ToString(CultureInfo.InvariantCulture);
            var effectiveProvider = form.GetValueOrDefault("ai_provider") is { Length: > 0 } p ? p
                : (new SettingsStore(db).Get("ai_provider") is { Length: > 0 } stored ? stored : "gemini");
            secrets.Add((effectiveProvider.ToLowerInvariant(), Secret("api_key")));
            secrets.Add(("tokenharbor", Secret("tokenharbor_api_key")));
            foreach (var key in new[] { "adzuna_app_id", "adzuna_app_key", "apify_token" }) secrets.Add((key, Secret(key)));
            notices.Add("IA e integrações salvas.");
        }
        if (Has("automation"))
        {
            foreach (var (key, lo, hi) in new[] { ("minimum_match_score", 0, 100), ("queue_max_workers", 1, 8), ("queue_max_attempts", 1, 200), ("queue_ttl_hours", 1, 168), ("transferable_minimum_score", 0, 100) })
                if (form.TryGetValue(key, out var v) && !Validate.InRange(v, lo, hi)) throw new ActionException($"Valor inválido para {key}: use {lo} a {hi}");
            if (form.ContainsKey("auto_apply")) form["auto_apply"] = form["auto_apply"] == "1" ? "1" : "0";
            var backup = Secret("backup_database_url").Trim();
            if (backup.Length > 0)
            {
                if (!backup.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) && !backup.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                    throw new ActionException("URL de backup inválida: use a URL do pooler da Supabase (postgresql://...)");
                secrets.Add(("backup_database_url", backup));
            }
            notices.Add(backup.Length > 0 ? "Automação e backup salvos." : "Automação salva.");
        }
        if (Has("smtp"))
        {
            if (form.TryGetValue("smtp_port", out var port) && !Validate.InRange(port, 1, 65535)) throw new ActionException("Porta SMTP inválida");
            secrets.Add(("smtp_password", Secret("smtp_password")));
            notices.Add("SMTP salvo.");
        }
        if (Has("profile"))
        {
            if (form.TryGetValue("portfolio_profile_url", out var url) && url.Trim().Length > 0)
            {
                form["portfolio_profile_url"] = url.Trim();
                if (!Uri.TryCreate(form["portfolio_profile_url"], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new ActionException("URL do portfólio inválida: use http(s)://...");
            }
            notices.Add("Perfil salvo.");
        }

        new SettingsStore(db).Save(form);
        foreach (var (name, value) in secrets)
        {
            if (value.Length == 0) continue; // vazio = manter o segredo atual
            try { SecretStore.Set(name, value); }
            catch (Exception ex) { throw new ActionException($"Não foi possível gravar no Cofre de Credenciais do Windows: {ex.Message}"); }
        }
        return notices.Count == 0 ? "Nada para salvar." : string.Join(' ', notices);
    }

    /// <summary>"a, b\nc" vira ["a","b","c"] sem repetir (<c>parse_model_list</c>).</summary>
    public static List<string> ParseModelList(string? raw) =>
        (raw ?? "").Replace("\n", ",").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

    /// <summary>Slider 0–100; vazio ou inválido volta para 75 (<c>job_selection_optimism_instruction</c>).</summary>
    public static int NormalizeOptimism(string? value)
    {
        var text = string.IsNullOrEmpty(value) ? "75" : value;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) && !double.IsInfinity(d)
            ? (int)Math.Max(0, Math.Min(100, Math.Truncate(d))) : 75;
    }

    private static void ValidateSearch(Dictionary<string, string> form)
    {
        if (form.TryGetValue("apify_job_count", out var count) && !Validate.InRange(count, 1, 100)) throw new ActionException("Máximo de vagas Apify deve ficar entre 1 e 100");
        if (form.TryGetValue("apify_monthly_credit_limit_usd", out var limit))
            if (!double.TryParse(limit.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || double.IsNaN(amount) || double.IsInfinity(amount) || amount < 0)
                throw new ActionException("Limite Apify inválido");
        if (form.TryGetValue("apify_actors_json", out var actors) && actors.Trim().Length > 0) ValidateActors(actors, form.GetValueOrDefault("apify_job_count"));
        if (form.TryGetValue("linkedin_main_share", out var share) && !Validate.InRange(share, 50, 95)) throw new ActionException("A fatia do filtro Principal deve ficar entre 50 e 95");
        if (form.TryGetValue("linkedin_max_age_days", out var age) && !Validate.InRange(age, 1, 30)) throw new ActionException("A idade máxima das vagas deve ficar entre 1 e 30 dias");
        if (form.TryGetValue("linkedin_preferred_countries", out var countries))
        {
            var places = Geo.ParsePreferred(countries);
            if (places.Count > 5) throw new ActionException("Informe no máximo 5 países com viés");
            if (places.Any(p => !CountryName().IsMatch(p.Name) || (p.City.Length > 0 && !CityName().IsMatch(p.City))))
                throw new ActionException("Países com viés: use nomes de países separados por vírgula, com a cidade opcional entre parênteses (ex.: Brazil (Belo Horizonte), Portugal)");
            form["linkedin_preferred_countries"] = string.Join(", ", places.Select(p => p.City.Length > 0 ? $"{p.Name} ({p.City})" : p.Name));
        }
        if (form.ContainsKey("geo_eligibility_enabled")) form["geo_eligibility_enabled"] = form["geo_eligibility_enabled"] == "1" ? "1" : "0";
        // O prompt igual ao padrão não é guardado: assim o painel acompanha o padrão quando ele mudar.
        if (form.TryGetValue("linkedin_filter_prompt", out var mainPrompt) && mainPrompt.Trim() == FilterPrompts.DefaultMain.Trim()) form["linkedin_filter_prompt"] = "";
        if (form.TryGetValue("transferable_prompt", out var secondaryPrompt) && secondaryPrompt.Trim() == FilterPrompts.DefaultSecondary.Trim()) form["transferable_prompt"] = "";
        foreach (var slot in new[] { "main", "secondary" })
            if (form.TryGetValue(LinkedInFilters.KeyFor(slot), out var filterJson)) form[LinkedInFilters.KeyFor(slot)] = LinkedInFilters.ValidateForm(filterJson, slot);
    }

    /// <summary>Lista JSON de actors com pelo menos um item, cada um um objeto com id (<c>load_apify_actors</c>).</summary>
    private static void ValidateActors(string raw, string? _)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw.Trim()); }
        catch (JsonException ex) { throw new ActionException($"JSON de actors Apify inválido: {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                throw new ActionException("apify_actors_json deve ser uma lista JSON com pelo menos um actor.");
            var index = 0;
            foreach (var item in root.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object) throw new ActionException($"Actor Apify #{index} deve ser um objeto JSON.");
                var id = item.TryGetProperty("id", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()!.Trim() : "";
                if (id.Length == 0) throw new ActionException($"Actor Apify #{index} sem id.");
            }
        }
    }
}
