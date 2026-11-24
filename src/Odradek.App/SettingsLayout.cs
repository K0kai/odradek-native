using Odradek.Core;

namespace Odradek.App;

/// <summary>Como um valor é mostrado: texto, ligado/desligado, segredo (só "configurada") ou opção de lista.</summary>
public enum FieldKind { Text, Toggle, Secret, Select, Filter, Sources }

public sealed record SettingField(string Key, string Label, FieldKind Kind = FieldKind.Text, string Secret = "", (string Value, string Label)[]? Options = null);

public sealed record SettingSection(string Title, string Description, SettingField[] Fields);

public sealed record SettingTab(string Label, SettingSection[] Sections);

/// <summary>Abas e seções de Configurações (mesmos títulos e rótulos de <c>Settings.tsx</c>); só leitura nesta fase.</summary>
public static class SettingsLayout
{
    private static readonly (string, string) NotInformed = ("not_informed", "Prefiro não informar");

    public static readonly SettingTab[] Tabs =
    [
        new("Busca",
        [
            new("Filtro principal", "TI e programação, a área de maior experiência. Sem ele a coleta no LinkedIn não roda.",
                [new("linkedin_main_filter_json", "Filtro principal", FieldKind.Filter)]),
            new("Filtro secundário", "Opcional: uma área de reserva, menos afetada por crises.",
                [new("linkedin_secondary_filter_json", "Filtro secundário", FieldKind.Filter), new("secondary_area_hint", "Dica da área para a IA")]),
            new("Fontes de vagas", "",
                [new("sources", "Onde buscar", FieldKind.Sources), new("apify_monthly_credit_limit_usd", "Limite mensal do Apify (USD)"),
                 new("keywords", "Palavras-chave"), new("adzuna_countries", "Países do Adzuna")]),
            new("Alcance", "",
                [new("linkedin_max_age_days", "Idade máxima das vagas do LinkedIn (dias)"), new("linkedin_preferred_countries", "Países preferidos (até 5)"),
                 new("geo_eligibility_enabled", "Respeitar elegibilidade geográfica", FieldKind.Toggle)]),
            new("Avançado", "",
                [new("locations", "Locais extras dos actors personalizados"), new("linkedin_main_share", "Fatia do filtro principal (%)"),
                 new("apify_job_count", "Vagas por busca (padrão)"), new("apify_actors_json", "Actors do Apify (JSON)"),
                 new("linkedin_filter_prompt", "Instruções da IA — filtro principal"), new("transferable_prompt", "Instruções da IA — filtro secundário")]),
        ]),
        new("Triagem",
        [
            new("Decisão da IA", "",
                [new("auto_apply", "Triar vagas novas automaticamente após cada coleta", FieldKind.Toggle),
                 new("minimum_match_score", "Score mínimo — área principal (%)"), new("transferable_minimum_score", "Score mínimo — área secundária (%)"),
                 new("job_selection_optimism", "Tolerância da seleção (0 rígido a 100 flexível)")]),
            new("Avançado", "",
                [new("job_selection_prompt", "Instruções da seleção"), new("queue_max_workers", "Triagens simultâneas"),
                 new("queue_max_attempts", "Tentativas por vaga"), new("queue_ttl_hours", "Validade na fila (horas)")]),
        ]),
        new("Perfil",
        [
            new("Contato", "",
                [new("candidate_name", "Nome"), new("candidate_email", "E-mail"), new("candidate_phone", "Telefone"),
                 new("candidate_linkedin", "LinkedIn"), new("candidate_current_company", "Empresa atual"), new("candidate_cpf", "CPF")]),
            new("Endereço", "A cidade e o país definem a elegibilidade geográfica e onde o LinkedIn é pesquisado.",
                [new("candidate_city", "Cidade"), new("candidate_state", "Estado"), new("candidate_country", "País"),
                 new("candidate_postal_code", "CEP"), new("candidate_street", "Endereço")]),
            new("Pretensão e contratação", "",
                [new("salary_expectation_brl", "Pretensão CLT (BRL)"), new("salary_expectation_usd", "Pretensão (USD)"),
                 new("candidate_contract_type", "Contratação preferida", FieldKind.Select,
                     Options: [("employee", "CLT / efetivo"), ("contractor", "PJ / contractor"), ("ask", "Perguntar")])]),
            new("Experiência para a IA", "Fatos que a IA pode usar sobre você na triagem e nas cartas.",
                [new("candidate_facts_pt", "Fatos profissionais (português)"), new("candidate_facts_en", "Professional facts (English)"),
                 new("portfolio_profile_url", "URL do portfólio")]),
            new("Diversidade (opcional)", "",
                [new("candidate_gender", "Gênero", FieldKind.Select, Options: [NotInformed, ("female", "Mulher"), ("male", "Homem"), ("other", "Outro / não binário")]),
                 new("candidate_race", "Raça / etnia", FieldKind.Select, Options: [NotInformed, ("white", "Branca"), ("black", "Preta"), ("pardo", "Parda"), ("asian", "Amarela / asiática"), ("indigenous", "Indígena")]),
                 new("candidate_pcd", "PCD", FieldKind.Select, Options: [NotInformed, ("no", "Não"), ("yes", "Sim")]),
                 new("candidate_lgbtq", "LGBTQ+", FieldKind.Select, Options: [NotInformed, ("no", "Hétero / cis"), ("gay", "Gay"), ("lesbian", "Lésbica"), ("bisexual", "Bissexual"), ("pansexual", "Pansexual"), ("asexual", "Assexual"), ("other", "Outra"), ("yes", "Sim, genérico")]),
                 new("candidate_diversity_note", "Nota de diversidade")]),
        ]),
        new("Contas e chaves",
        [
            new("Inteligência artificial", "",
                [new("ai_provider", "Provedor", FieldKind.Select, Options: [("gemini", "Gemini"), ("openai", "OpenAI")]),
                 new("ai_model", "Modelo"), new("api_key", "Chave da IA", FieldKind.Secret, Secret: "@provider")]),
            new("TokenHarbor (fallback gratuito)", "Último recurso: só é usado quando todos os modelos do provedor acima falharam.",
                [new("tokenharbor_api_key", "Chave do TokenHarbor", FieldKind.Secret, Secret: "tokenharbor"),
                 new("tokenharbor_fallback_models", "Modelos gratuitos (em ordem)")]),
            new("Apify e Adzuna", "Credenciais das fontes de vagas.",
                [new("apify_token", "Apify token", FieldKind.Secret, Secret: "apify_token"),
                 new("backup_database_url", "Backup na nuvem (URL do banco)", FieldKind.Secret, Secret: "backup_database_url"),
                 new("adzuna_app_id", "Adzuna App ID", FieldKind.Secret, Secret: "adzuna_app_id"),
                 new("adzuna_app_key", "Adzuna API key", FieldKind.Secret, Secret: "adzuna_app_key")]),
            new("E-mail (SMTP)", "Também é usado para enviar candidaturas por e-mail a vagas aprovadas que trazem um endereço de contato.",
                [new("smtp_host", "Servidor"), new("smtp_port", "Porta"), new("smtp_user", "Usuário"), new("smtp_from", "Remetente"),
                 new("smtp_password", "Senha", FieldKind.Secret, Secret: "smtp_password"), new("smtp_use_tls", "Usar TLS", FieldKind.Toggle)]),
            new("Extensão do navegador", "Pareamento com a extensão Odradek Autofill: gere o token aqui e cole-o na extensão.",
                [new("__extension_token_hash__", "Token da extensão", FieldKind.Secret, Secret: "")]),
        ]),
    ];

    /// <summary>Campos de texto longo (várias linhas) na edição.</summary>
    public static readonly HashSet<string> MultiLine =
    [
        "keywords", "locations", "apify_actors_json", "linkedin_filter_prompt", "transferable_prompt", "job_selection_prompt",
        "candidate_facts_pt", "candidate_facts_en", "candidate_diversity_note",
    ];

    /// <summary>Campos com controle deslizante (mínimo, máximo, padrão).</summary>
    public static readonly Dictionary<string, (int Min, int Max, int Default)> Sliders = new()
    {
        ["linkedin_max_age_days"] = (1, 30, 7), ["job_selection_optimism"] = (0, 100, 75),
    };

    public static readonly (string Id, string Label)[] SourceOptions =
        [("apify", "LinkedIn (via Apify)"), ("remotive", "Remotive"), ("remoteok", "RemoteOK"), ("adzuna", "Adzuna")];

    /// <summary>Quanto dos requisitos obrigatórios a vaga precisa cobrir para a tolerância dada (curva por trechos de <c>Settings.tsx</c>).</summary>
    public static int Coverage(int value)
    {
        (int X, int Y)[] curve = [(0, 100), (50, 80), (75, 70), (100, 55)];
        for (var i = 1; i < curve.Length; i++)
            if (value <= curve[i].X) return (int)Math.Round(curve[i - 1].Y + (double)(curve[i].Y - curve[i - 1].Y) * (value - curve[i - 1].X) / (curve[i].X - curve[i - 1].X), MidpointRounding.AwayFromZero);
        return curve[^1].Y;
    }

    private static readonly string[] SourceLabels = ["apify=LinkedIn (via Apify)", "remotive=Remotive", "remoteok=RemoteOK", "adzuna=Adzuna"];

    /// <summary>Nome e palavras-chave do filtro gerado; se o JSON não for lido, só o tamanho.</summary>
    private static string FilterSummary(string raw)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(raw) is System.Text.Json.Nodes.JsonObject filter)
            {
                var keywords = (filter["keywords"] as System.Text.Json.Nodes.JsonArray ?? []).Select(k => Text.PyStrRaw(k)).Where(k => k.Length > 0).ToList();
                var name = Text.PyStr(filter["name"]);
                var reason = Text.PyStr(filter["reason"]);
                var lines = new List<string> { (name.Length > 0 ? name + ": " : "") + (keywords.Count > 0 ? string.Join(" / ", keywords) : "sem palavras-chave") };
                if (reason.Length > 0) lines.Add(reason);
                return string.Join(Environment.NewLine, lines);
            }
        }
        catch (System.Text.Json.JsonException) { }
        return $"gerado ({raw.Length} caracteres)";
    }

    /// <summary>O valor como aparece na tela. Segredos nunca são lidos para a tela: só "configurada" ou "não configurada".</summary>
    public static string Display(SettingField field, IReadOnlyDictionary<string, string> values)
    {
        values.TryGetValue(field.Key, out var raw);
        raw ??= "";
        switch (field.Kind)
        {
            case FieldKind.Secret:
                if (field.Key == "__extension_token_hash__") return raw.Length > 0 ? "configurado" : "não gerado";
                var name = field.Secret == "@provider" ? (values.TryGetValue("ai_provider", out var p) && p.Length > 0 ? p.ToLowerInvariant() : "gemini") : field.Secret;
                return SecretStore.Has(name) ? "configurada" : "não configurada";
            case FieldKind.Toggle:
                return raw == "1" ? "Ligado" : "Desligado"; // o painel grava sempre "1" ou "0"
            case FieldKind.Select:
                return field.Options?.FirstOrDefault(o => o.Value == raw).Label is { } label ? label : raw;
            case FieldKind.Sources:
                var ids = raw.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
                return ids.Count == 0 ? "—" : string.Join(", ", ids.Select(id => SourceLabels.FirstOrDefault(l => l.StartsWith(id + "=", StringComparison.Ordinal))?.Split('=')[1] ?? id));
            case FieldKind.Filter:
                return raw.Trim().Length == 0 ? "não gerado" : FilterSummary(raw);
            default:
                if (raw.Length == 0) return "—";
                return raw.Length > 240 ? Text.Take(raw, 240) + $"… ({raw.Length} caracteres)" : raw;
        }
    }
}
