using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.Core.Api;

/// <summary>Rotas que a extensão do navegador chama com o token de pareamento (<c>web/extension_token.py:EXTENSION_PATHS</c>).</summary>
public sealed partial class ExtensionApi(Database db, Func<Assistant> assistant, Func<HttpClient>? http = null, LinkedInQueue? queue = null)
{
    public static readonly IReadOnlyList<string> Paths = ["/api/assistant", "/api/autofill-profile", "/api/autofill-rules", "/api/autofill-resume", "/api/linkedin-import"];

    /// <summary>
    /// Recebe da extensão as vagas da página do LinkedIn do usuário (ex.: "Recomendadas para você"), busca os detalhes no endpoint público
    /// e as grava como vagas novas. Corpo: <c>{"jobs":[{"id","title","company","location"}]}</c>.
    /// </summary>
    public Task<ApiResult> ImportLinkedInAsync(JsonObject body, CancellationToken ct = default) => LinkedInImport.HandleAsync(db, body, http, ct, queue);

    private static readonly (string Rule, string Field)[] ExtensionKeys =
    [
        ("full_name", "fullName"), ("email", "email"), ("phone", "phone"), ("phone_country_code", "phoneCountryCode"), ("linkedin", "linkedin"), ("city", "city"), ("street_address", "streetAddress"),
        ("postal_code", "zip"), ("state", "state"), ("country", "country"), ("current_company", "currentCompany"), ("cpf", "cpf"),
        ("salary", "salaryExpectation"), ("contract_type", "contractType"),
    ];

    [GeneratedRegex(@"[,\n;]")] private static partial Regex AliasSeparators();

    public ApiResult AutofillProfile()
    {
        var cfg = new SettingsStore(db).All();
        string Value(string key) => (cfg.GetValueOrDefault(key) ?? "").Trim();
        var name = Value("candidate_name");
        var parts = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // O telefone vai de duas formas: inteiro em "phone" (+DDI quando o DDI é conhecido) e separado em DDI + número nacional, para os sites
        // que têm um campo para cada. Sem telefone, nada disso vai (a extensão não deve preencher só o código do país).
        var (countryCode, national) = PhoneNumber.Split(Value("candidate_phone"), Value("candidate_phone_country_code"));
        var phone = countryCode.Length > 0 ? "+" + countryCode + national : Value("candidate_phone");
        return ApiResult.Ok(new JsonObject
        {
            ["profile"] = new JsonObject
            {
                ["firstName"] = parts.Length > 0 ? parts[0] : "", ["lastName"] = string.Join(' ', parts.Skip(1)), ["fullName"] = name,
                ["email"] = Value("candidate_email"), ["phone"] = phone, ["phoneCountryCode"] = countryCode, ["phoneNational"] = national, ["city"] = Value("candidate_city"), ["state"] = Value("candidate_state"),
                ["country"] = Value("candidate_country"), ["zip"] = Value("candidate_postal_code"), ["linkedin"] = Value("candidate_linkedin"),
            },
        });
    }

    public ApiResult AutofillRules()
    {
        List<JsonObject> rules;
        using (var connection = db.Open()) rules = Rows.Query(connection, "SELECT * FROM form_field_rules ORDER BY sort_order, id");
        var fields = new JsonArray();
        foreach (var rule in rules)
        {
            var key = ExtensionKeys.FirstOrDefault(k => k.Rule == Text.PyStr(rule["key"]).ToLowerInvariant()).Field;
            if (key is null || Text.PyStr(rule["mode"]).ToLowerInvariant() == "skip") continue;
            var aliases = AliasSeparators().Split(Text.PyStr(rule["aliases"])).Select(a => a.Trim()).Where(a => a.Length > 0);
            fields.Add(new JsonObject { ["key"] = key, ["aliases"] = new JsonArray(aliases.Select(a => (JsonNode?)a).ToArray()) });
        }
        return ApiResult.Ok(new JsonObject { ["fields"] = fields });
    }

    public ApiResult AutofillResume(string language)
    {
        using var connection = db.Open();
        if (connection.ExecuteScalar<long?>("SELECT id FROM resumes WHERE language=@language", new { language }) is null)
            return ApiResult.Error("Currículo não cadastrado neste idioma.", 404);
        return ResumeActions.ReadPdf(connection, language) is { } pdf ? ApiResult.Pdf(pdf) : ApiResult.Error("Arquivo de currículo indisponível.", 404);
    }

    /// <summary>Responde a pergunta de um formulário. Validação e IA indisponível seguem os códigos do Python (400 e 503).</summary>
    public async Task<ApiResult> AssistantAsync(string? message, string? history, CancellationToken ct = default)
    {
        try
        {
            var answer = await assistant().AnswerAsync((message ?? "").Trim(), JsonValue.Create(string.IsNullOrEmpty(history) ? "[]" : history), ct).ConfigureAwait(false);
            return ApiResult.Ok(new JsonObject { ["ok"] = true, ["answer"] = answer });
        }
        catch (AiUnavailableException ex) { return ApiResult.Error(ex.Message, 503); }
        catch (ActionException ex) { return ApiResult.Error(ex.Message); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return ApiResult.Error("Falha ao gerar resposta.", 500);
        }
    }
}
