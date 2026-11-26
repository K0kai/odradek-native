using System.Text.Json.Nodes;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.Core.Api;

/// <summary>Rotas que a extensão do navegador chama com o token de pareamento (<c>web/extension_token.py:EXTENSION_PATHS</c>).</summary>
public sealed class ExtensionApi(Database db, Func<Assistant> assistant, Func<HttpClient>? http = null, LinkedInQueue? queue = null, IndeedQueue? indeedQueue = null)
{
    public static readonly IReadOnlyList<string> Paths = ["/api/assistant", "/api/autofill-resume", "/api/linkedin-import", "/api/indeed-import"];

    /// <summary>Vagas do Indeed lidas pela extensão na aba do usuário (já com descrição). Ver <see cref="IndeedImport.Handle"/>.</summary>
    public ApiResult ImportIndeed(JsonObject body) => IndeedImport.Handle(db, body, indeedQueue);

    /// <summary>
    /// Recebe da extensão as vagas da página do LinkedIn do usuário (ex.: "Recomendadas para você"), busca os detalhes no endpoint público
    /// e as grava como vagas novas. Corpo: <c>{"jobs":[{"id","title","company","location"}]}</c>.
    /// </summary>
    public Task<ApiResult> ImportLinkedInAsync(JsonObject body, CancellationToken ct = default) => LinkedInImport.HandleAsync(db, body, http, ct, queue);

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
