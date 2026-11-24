using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Odradek.Core.Actions;
using Odradek.Core.Api;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Engines;

namespace Odradek.LocalApi;

/// <summary>O que as rotas precisam do app. Funções, não instâncias: os motores só são montados quando uma rota os usa.</summary>
public sealed class ApiServices(Database db, Func<JobCollector> collector, Func<Assistant> assistant, Func<string> qpidToken)
{
    public Database Db { get; } = db;
    public QpidApi Qpid() => new(Db, collector());
    public ExtensionApi Extension() => new(Db, assistant);
    public string QpidToken() => qpidToken();
}

/// <summary>
/// Rotas da extensão do navegador e do Qpid em 127.0.0.1:8765 (<c>web/api_asgi.py</c>): mesma autenticação, mesmos métodos, mesmos cabeçalhos.
/// O painel web não existe mais; qualquer outro caminho responde 404.
/// </summary>
public static partial class ApiRoutes
{
    [GeneratedRegex("^(pt|en)$")] private static partial Regex LanguagePattern();

    private sealed class Json(ApiResult result) : IResult
    {
        public Task ExecuteAsync(HttpContext context) => Write(context, result);
    }

    private static readonly JsonSerializerOptions Options = new();

    public static async Task Write(HttpContext context, ApiResult result)
    {
        context.Response.StatusCode = result.Status;
        if (result.Headers is not null) foreach (var (name, value) in result.Headers) context.Response.Headers[name] = value;
        if (result.File is { } file)
        {
            context.Response.ContentType = result.ContentType ?? "application/octet-stream";
            context.Response.ContentLength = file.Length;
            await context.Response.Body.WriteAsync(file);
            return;
        }
        context.Response.ContentType = "application/json; charset=utf-8";
        var text = result.BodyText ?? "null";
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes);
    }

    private static IResult Reply(ApiResult result) => new Json(result);

    private static bool ParseId(string raw, out long id, out IResult? error)
    {
        error = null;
        if (long.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out id)) return true;
        error = Reply(ApiResult.Invalid("path", "job_id", "Input should be a valid integer, unable to parse string as an integer", "int_parsing"));
        return false;
    }

    private static string? Language(HttpRequest request, out IResult? error)
    {
        error = null;
        var language = request.Query.TryGetValue("language", out var values) && values.Count > 0 ? values[0] ?? "pt" : "pt";
        if (LanguagePattern().IsMatch(language)) return language;
        error = Reply(ApiResult.Invalid("query", "language", "String should match pattern '^(pt|en)$'", "string_pattern_mismatch"));
        return null;
    }

    private static bool Limit(HttpRequest request, int fallback, int max, out int limit, out IResult? error)
    {
        error = null;
        limit = fallback;
        if (!request.Query.TryGetValue("limit", out var values) || values.Count == 0) return true;
        if (!int.TryParse(values[0], out limit))
        {
            error = Reply(ApiResult.Invalid("query", "limit", "Input should be a valid integer, unable to parse string as an integer", "int_parsing"));
            return false;
        }
        if (limit < 1 || limit > max)
        {
            error = Reply(ApiResult.Invalid("query", "limit", limit < 1 ? "Input should be greater than or equal to 1" : $"Input should be less than or equal to {max}", limit < 1 ? "greater_than_equal" : "less_than_equal"));
            return false;
        }
        return true;
    }

    private static bool FromStart(HttpRequest request, out bool fromStart, out IResult? error)
    {
        error = null;
        fromStart = false;
        var position = request.Query.TryGetValue("position", out var values) && values.Count > 0 ? values[0] ?? "fim" : "fim";
        if (position is "inicio" or "fim") { fromStart = position == "inicio"; return true; }
        error = Reply(ApiResult.Invalid("query", "position", "String should match pattern '^(inicio|fim)$'", "string_pattern_mismatch"));
        return false;
    }

    private static async Task<(string? Text, bool TooLarge)> ReadText(HttpRequest request, int maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > maxBytes) return (null, true);
            buffer.Write(chunk, 0, read);
        }
        return (Encoding.UTF8.GetString(buffer.ToArray()), false);
    }

    /// <summary>Corpo JSON obrigatório, que tem de ser um objeto (<c>body: dict = Body(...)</c>); senão 422 como o FastAPI.</summary>
    private static async Task<(JsonObject? Body, IResult? Error)> ReadObject(HttpRequest request)
    {
        var (text, tooLarge) = await ReadText(request, 1024 * 1024);
        if (tooLarge) return (null, Reply(new ApiResult(413, new JsonObject { ["detail"] = "Request Entity Too Large" })));
        try
        {
            if (JsonNode.Parse(text ?? "") is JsonObject body) return (body, null);
            return (null, Reply(ApiResult.Invalid("body", "body", "Input should be a valid dictionary", "dict_type")));
        }
        catch (JsonException)
        {
            return (null, Reply(ApiResult.Invalid("body", "body", "JSON decode error", "json_invalid")));
        }
    }

    public static void Map(WebApplication app, ApiServices services)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Content-Security-Policy"] = "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
                var path = context.Request.Path.Value ?? "";
                if (path.StartsWith("/api/", StringComparison.Ordinal)) headers["Cache-Control"] = "no-store";
                return Task.CompletedTask;
            });
            if (context.Request.Method is not ("GET" or "HEAD" or "POST"))
            {
                await Write(context, ApiResult.Error("Método não permitido", 405));
                return;
            }
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/api/qpid/", StringComparison.Ordinal))
            {
                string token;
                try { token = services.QpidToken(); }
                catch (Exception) { token = ""; }
                if (token.Length == 0) { await Write(context, ApiResult.Error("Configure QPID_API_TOKEN.", 503)); return; }
                var supplied = context.Request.Headers.Authorization.ToString();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes("Bearer " + token)))
                {
                    await Write(context, ApiResult.Error("Token inválido.", 401));
                    return;
                }
            }
            else if (ExtensionApi.Paths.Contains(path))
            {
                // Sem o painel web não há sessão: as rotas da extensão só respondem com o token de pareamento.
                var bearer = context.Request.Headers.Authorization.ToString();
                var valid = bearer.Length >= 7 && bearer[..7].Equals("bearer ", StringComparison.OrdinalIgnoreCase)
                    && new ExtensionTokenActions(services.Db).Verify(bearer[7..].Trim());
                if (!valid)
                {
                    await Write(context, ApiResult.Error("Token da extensão inválido ou revogado. Gere outro no painel (Configurações).", 401));
                    return;
                }
            }
            await next();
        });

        app.MapGet("/health", () => Results.Text("{\"ok\":true,\"service\":\"" + LocalServer.ServiceName + "\"}", "application/json"));
        app.MapGet("/health/db", () =>
        {
            try
            {
                using var connection = services.Db.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                command.ExecuteScalar();
                return Results.Text("{\"ok\":true,\"service\":\"" + LocalServer.ServiceName + "\",\"database\":\"ok\"}", "application/json");
            }
            catch (Exception)
            {
                return Results.Text("{\"ok\":false,\"service\":\"" + LocalServer.ServiceName + "\",\"database\":\"unavailable\",\"reason\":\"unknown\"}", "application/json", statusCode: 503);
            }
        });

        // ---------------------------------------------------------------- extensão
        app.MapGet("/api/autofill-profile", () => Reply(services.Extension().AutofillProfile()));
        app.MapGet("/api/autofill-rules", () => Reply(services.Extension().AutofillRules()));
        app.MapGet("/api/autofill-resume", (HttpRequest request) =>
            Language(request, out var error) is { } language ? Reply(services.Extension().AutofillResume(language)) : error!);
        app.MapPost("/api/assistant", async (HttpContext context) =>
        {
            var contentType = context.Request.ContentType ?? "";
            var urlEncoded = contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.Ordinal);
            var multipart = contentType.StartsWith("multipart/form-data", StringComparison.Ordinal) && contentType.Contains("boundary=", StringComparison.Ordinal);
            if (!urlEncoded && !multipart) return Reply(ApiResult.Error("Formulário inválido"));
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 256 * 1024;
            IFormCollection form;
            try { form = await context.Request.ReadFormAsync(context.RequestAborted); }
            catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException or IOException)
            {
                return Reply(ApiResult.Error("Arquivo ou formulário muito grande"));
            }
            return Reply(await services.Extension().AssistantAsync(form["message"].ToString(), form["history"].ToString(), context.RequestAborted));
        });

        // ---------------------------------------------------------------- Qpid
        app.MapGet("/api/qpid/status", () => Reply(services.Qpid().Status()));
        app.MapGet("/api/qpid/capabilities", () => Reply(QpidApi.Capabilities()));
        app.MapGet("/api/qpid/queue", (HttpRequest request) =>
            Limit(request, 100, 500, out var limit, out var error) && FromStart(request, out var fromStart, out error) ? Reply(services.Qpid().Queue(limit, fromStart)) : error!);
        app.MapGet("/api/qpid/logs", (HttpRequest request) =>
            Limit(request, 30, 500, out var limit, out var error) && FromStart(request, out var fromStart, out error) ? Reply(services.Qpid().Logs(limit, fromStart)) : error!);
        app.MapPost("/api/qpid/collector/{action}", async (string action, HttpRequest request) =>
        {
            string? raw = null;
            if (action == "start")
            {
                var (text, tooLarge) = await ReadText(request, 1024);
                if (tooLarge) return Reply(ApiResult.Error("Corpo inválido"));
                raw = text;
            }
            return Reply(services.Qpid().CollectorAction(action, raw));
        });
        app.MapPost("/api/qpid/jobs/bulk-ignore/preview", async (HttpRequest request) =>
        {
            var (body, error) = await ReadObject(request);
            return body is null ? error! : Reply(services.Qpid().BulkIgnorePreview(body));
        });
        app.MapPost("/api/qpid/jobs/bulk-ignore/apply", async (HttpRequest request) =>
        {
            var (body, error) = await ReadObject(request);
            return body is null ? error! : Reply(services.Qpid().BulkIgnoreApply(body));
        });

        app.MapGet("/api/qpid/applications", (HttpRequest request) =>
            Limit(request, 30, 100, out var limit, out var error) ? Reply(services.Qpid().Candidates(limit)) : error!);
        app.MapGet("/api/qpid/applications/{jobId}", (string jobId, HttpRequest request) =>
            ParseId(jobId, out var id, out var error) && Language(request, out error) is { } language ? Reply(services.Qpid().Context(id, language)) : error!);
        app.MapGet("/api/qpid/applications/{jobId}/resume", (string jobId, HttpRequest request) =>
            ParseId(jobId, out var id, out var error) && Language(request, out error) is { } language ? Reply(services.Qpid().ResumeFile(id, language)) : error!);
        app.MapGet("/api/qpid/applications/{jobId}/results", (string jobId) =>
            ParseId(jobId, out var id, out var error) ? Reply(services.Qpid().Results(id)) : error!);
        app.MapPost("/api/qpid/applications/{jobId}/attempt", async (string jobId, HttpRequest request) =>
        {
            if (!ParseId(jobId, out var id, out var error)) return error!;
            var (body, bodyError) = await ReadObject(request);
            return body is null ? bodyError! : Reply(services.Qpid().Reserve(id, body));
        });
        app.MapPost("/api/qpid/applications/{jobId}/result", async (string jobId, HttpRequest request) =>
        {
            if (!ParseId(jobId, out var id, out var error)) return error!;
            var (body, bodyError) = await ReadObject(request);
            return body is null ? bodyError! : Reply(services.Qpid().Result(id, body));
        });
        app.MapPost("/api/qpid/applications/{jobId}/confirmed", async (string jobId, HttpRequest request) =>
        {
            if (!ParseId(jobId, out var id, out var error)) return error!;
            var (body, bodyError) = await ReadObject(request);
            return body is null ? bodyError! : Reply(services.Qpid().Confirmed(id, body));
        });

        app.MapFallback(() => Reply(new ApiResult(404, new JsonObject { ["detail"] = "Not Found" })));
    }
}
