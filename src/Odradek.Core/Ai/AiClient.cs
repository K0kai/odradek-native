using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Odradek.Core.Ai;

/// <summary>Provedor, modelo e chave de uma chamada (a chave vem do Cofre; nunca vai para log).</summary>
public sealed record AiCall(string Provider, string Model, string ApiKey);

/// <summary>Ajustes do transporte de IA. Os padrões são os do Python; os testes trocam pausa e relógio.</summary>
public sealed class AiClientOptions
{
    /// <summary>Modelos gratuitos tentados depois do configurado (só Gemini). <c>AI_FALLBACK_MODELS</c> substitui; vazio desliga.</summary>
    public IReadOnlyList<string> FallbackModels { get; init; } = EnvList("AI_FALLBACK_MODELS") ?? ["gemini-3.8-flash"];

    /// <summary>Pausa antes de cada nova rodada da cadeia quando só houve falha passageira. <c>AI_TRANSIENT_RETRIES</c> limita.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = EnvRetryDelays();

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan GoneCooldown { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan TokenHarborCache { get; init; } = TimeSpan.FromSeconds(30);
    public Func<TimeSpan, CancellationToken, Task> Pause { get; init; } = (span, ct) => Task.Delay(span, ct);
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    private static List<string>? EnvList(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw is null ? null : raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private static TimeSpan[] EnvRetryDelays()
    {
        TimeSpan[] all = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15)];
        var raw = Environment.GetEnvironmentVariable("AI_TRANSIENT_RETRIES");
        if (string.IsNullOrWhiteSpace(raw)) return all;
        return int.TryParse(raw.Trim(), out var rounds) ? all.Take(Math.Max(0, rounds)).ToArray() : all;
    }
}

/// <summary>
/// Único ponto de chamada HTTP para os provedores de IA (porte de <c>ai_client.py</c>): Gemini, OpenAI e, como último recurso,
/// os modelos gratuitos do TokenHarbor. Troca de modelo em sobrecarga, cooldown por modelo, rodadas com espera e reparo de JSON.
/// </summary>
public sealed partial class AiClient
{
    public const string TokenHarbor = "tokenharbor";
    public const string TokenHarborEndpoint = "https://tokenharbor.ai/v1/chat/completions";
    private static readonly HashSet<int> QuotaStatuses = [401, 403, 429, 502, 503, 504];
    private static readonly HashSet<int> FallbackStatuses = [429, 500, 502, 503, 504, 404];
    private static readonly HashSet<int> TransientStatuses = [500, 502, 503, 504];
    private const int RateLimit = 429;
    private const int Gone = 404;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly UsageTracker? _usage;
    private readonly Func<(string Key, IReadOnlyList<string> Models)>? _tokenHarborSource;
    private readonly AiClientOptions _options;
    private readonly Dictionary<(string, string), DateTime> _cooldown = [];
    private readonly object _gate = new();
    private (DateTime Until, (string Key, IReadOnlyList<string> Models) Value)? _thCache;

    public AiClient(HttpClient http, UsageTracker? usage = null, Func<(string Key, IReadOnlyList<string> Models)>? tokenHarborSource = null, AiClientOptions? options = null)
    {
        _http = http;
        _usage = usage;
        _tokenHarborSource = tokenHarborSource;
        _options = options ?? new AiClientOptions();
    }

    /// <summary>HttpClient sem o limite global de 100 s: cada chamada impõe o próprio prazo.</summary>
    public static HttpClient CreateHttp() => new() { Timeout = Timeout.InfiniteTimeSpan };

    // ---------------------------------------------------------------- API pública

    public Task<string> TextAsync(string prompt, AiCall call, int maxOutputTokens = 450, double temperature = 0.2, int timeoutSeconds = 60,
        Action<string>? onModel = null, CancellationToken ct = default) =>
        WithFallbackAsync(call, async (provider, model) =>
        {
            var reply = await RequestAsync(prompt, provider, model, CredentialsFor(provider, call), maxOutputTokens,
                provider is "gemini" or TokenHarbor ? temperature : null, timeoutSeconds, null, null, true, ct);
            return reply.Text;
        }, onModel, ct);

    public Task<JsonObject> JsonAsync(string prompt, JsonObject schema, AiCall call, int maxOutputTokens = 4096, double temperature = 0.0,
        int? thinkingBudget = null, int timeoutSeconds = 90, Action<string>? onModel = null, CancellationToken ct = default) =>
        WithFallbackAsync(call, (provider, model) => JsonOnceAsync(prompt, schema, provider, model, CredentialsFor(provider, call), maxOutputTokens,
            temperature, thinkingBudget, timeoutSeconds, ct), onModel, ct);

    /// <summary>"tokenharbor/modelo" (rótulo devolvido por <c>onModel</c> quando o último recurso respondeu) vira (tokenharbor, modelo).</summary>
    public static (string Provider, string Model) SplitModelLabel(string provider, string label)
    {
        var parts = (label ?? "").Split('/', 2);
        return parts.Length == 2 && parts[0] == TokenHarbor && parts[1].Length > 0 ? (TokenHarbor, parts[1]) : (provider, label ?? "");
    }

    public static List<string> ParseModelList(string? raw) =>
        (raw ?? "").Replace("\n", ",").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();

    /// <summary>Estrutura em modo estrito: todo objeto fecha as propriedades e as exige todas (exigência da OpenAI, inofensivo no Gemini).</summary>
    public static JsonObject Strictify(JsonObject schema) => (JsonObject)Walk(schema)!;

    private static JsonNode? Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in obj) copy[key] = Walk(value);
                if (copy["type"] is JsonValue type && type.GetValueKind() == JsonValueKind.String && type.GetValue<string>() == "object" && copy["properties"] is JsonObject props)
                {
                    copy["additionalProperties"] = false;
                    copy["required"] = new JsonArray(props.Select(kv => (JsonNode?)JsonValue.Create(kv.Key)).ToArray());
                }
                return copy;
            }
            case JsonArray array:
                return new JsonArray(array.Select(Walk).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    // ---------------------------------------------------------------- TokenHarbor, cooldown e cadeia de modelos

    private (string Key, IReadOnlyList<string> Models) TokenHarborConfig()
    {
        if (_tokenHarborSource is null) return ("", []);
        lock (_gate)
            if (_thCache is { } cached && cached.Until > _options.UtcNow()) return cached.Value;
        (string Key, IReadOnlyList<string> Models) value;
        try
        {
            var (key, models) = _tokenHarborSource();
            value = ((key ?? "").Trim(), models ?? []);
        }
        catch (Exception) { value = ("", []); }
        lock (_gate) _thCache = (_options.UtcNow() + _options.TokenHarborCache, value);
        return value;
    }

    /// <summary>Descarta a chave guardada (chave nova no painel: a próxima leitura é de verdade).</summary>
    public void ResetTokenHarborCache() { lock (_gate) _thCache = null; }

    private bool CoolingDown(string provider, string model)
    {
        lock (_gate) return _cooldown.TryGetValue((provider, model), out var until) && until > _options.UtcNow();
    }

    private void StartCooldown(string provider, string model, TimeSpan span)
    {
        lock (_gate) _cooldown[(provider, model)] = _options.UtcNow() + span;
    }

    /// <summary>Modelo configurado primeiro; fallbacks depois; quem falhou há pouco vai para o fim.</summary>
    public List<string> ModelChain(string provider, string model)
    {
        provider = provider.ToLowerInvariant().Trim();
        if (provider != "gemini") return [model];
        var chain = new[] { model }.Concat(_options.FallbackModels.Where(m => !string.Equals(m, model, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
        return chain.Where(m => !CoolingDown(provider, m)).Concat(chain.Where(m => CoolingDown(provider, m))).ToList();
    }

    private static bool ShouldFallBack(AiException exc) =>
        exc is AiResponseException || exc.Transient || (exc.HttpStatus is { } s && FallbackStatuses.Contains(s));

    private static bool IsTransient(AiException exc) => exc.Transient || (exc.HttpStatus is { } s && TransientStatuses.Contains(s));

    private static bool WorthAnotherRound(List<AiException> errors) =>
        errors.Any(IsTransient) && errors.All(e => IsTransient(e) || e.HttpStatus == RateLimit);

    private static string FailureLabel(AiException exc) =>
        exc is AiResponseException ? "resposta inutilizável" : exc.HttpStatus is { } s ? $"HTTP {s}" : exc.GetType().Name;

    private string CredentialsFor(string callProvider, AiCall primary) =>
        callProvider == primary.Provider.ToLowerInvariant().Trim() ? primary.ApiKey : TokenHarborConfig().Key;

    private async Task<T> WalkChainAsync<T>(AiCall call, Func<string, string, Task<T>> attempt, Action<string>? onModel)
    {
        var primary = call.Provider.ToLowerInvariant().Trim();
        var chain = ModelChain(call.Provider, call.Model).Select(name => (Provider: primary, Name: name)).ToList();
        var extrasLoaded = primary == TokenHarbor;
        var failures = new List<(string Label, AiException Error)>();
        var weighed = new List<AiException>();
        var index = 0;
        while (index < chain.Count)
        {
            var (provider, candidate) = chain[index];
            var label = provider == primary ? candidate : $"{provider}/{candidate}";
            T result;
            try { result = await attempt(provider, candidate); }
            catch (AiException exc)
            {
                // Falha do último recurso (chave/permissão do TokenHarbor) não mascara o erro do provedor principal.
                if (provider == primary && !ShouldFallBack(exc)) throw;
                failures.Add((label, exc));
                if (provider == primary || IsTransient(exc) || exc.HttpStatus == RateLimit) weighed.Add(exc);
                if (exc is not AiResponseException)
                    StartCooldown(provider, candidate, exc.HttpStatus == Gone ? _options.GoneCooldown : _options.Cooldown);
                index++;
                if (index == chain.Count && !extrasLoaded)
                {
                    extrasLoaded = true;
                    var (key, models) = TokenHarborConfig();
                    if (key.Length > 0)
                        chain.AddRange(models.Where(m => !CoolingDown(TokenHarbor, m)).Concat(models.Where(m => CoolingDown(TokenHarbor, m))).Select(m => (TokenHarbor, m)));
                }
                if (index < chain.Count) continue;
                var summary = string.Join("; ", failures.Select(f => $"{f.Label}: {FailureLabel(f.Error)}"));
                var message = $"IA indisponível: nenhum modelo respondeu ({summary}).";
                AiException final = exc is AiResponseException
                    ? new AiResponseException(message, exc)
                    : new AiUnavailableException(message, exc) { HttpStatus = exc.HttpStatus };
                final.ChainTransient = WorthAnotherRound(weighed);
                throw final;
            }
            onModel?.Invoke(label);
            return result;
        }
        throw new AiUnavailableException("IA indisponível: nenhum modelo respondeu.");
    }

    /// <summary>Passa pela cadeia; se todas as falhas foram passageiras, espera (com jitter) e repete.</summary>
    private async Task<T> WithFallbackAsync<T>(AiCall call, Func<string, string, Task<T>> attempt, Action<string>? onModel, CancellationToken ct)
    {
        var delays = _options.RetryDelays;
        for (var round = 0; round <= delays.Count; round++)
        {
            try { return await WalkChainAsync(call, attempt, onModel); }
            catch (AiException exc) when (round < delays.Count && exc.ChainTransient)
            {
                await _options.Pause(delays[round] * (0.75 + Random.Shared.NextDouble() * 0.5), ct); // workers paralelos não voltam em sincronia
            }
        }
        throw new AiUnavailableException("IA indisponível: nenhum modelo respondeu.");
    }

    // ---------------------------------------------------------------- uma chamada HTTP

    private sealed record Reply(string Text, bool Truncated);

    private void Record(string provider, string model, JsonObject? result, bool ok, bool quotaError) =>
        _usage?.Record(provider, model, result, ok, quotaError);

    private async Task<Reply> RequestAsync(string prompt, string provider, string model, string apiKey, int maxOutputTokens, double? temperature,
        int timeoutSeconds, JsonObject? schema, int? thinkingBudget, bool useSchema, CancellationToken ct)
    {
        if (apiKey.Length == 0) throw new AiUnavailableException("IA indisponivel: chave nao configurada.");
        provider = provider.ToLowerInvariant().Trim();
        string endpoint;
        JsonObject body;
        string? bearer = null;
        switch (provider)
        {
            case "openai":
                body = new JsonObject { ["model"] = model, ["input"] = prompt, ["store"] = false, ["max_output_tokens"] = maxOutputTokens };
                if (schema is not null)
                    body["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = "result", ["strict"] = true, ["schema"] = Strictify(schema) } };
                if (temperature is { } t1 && model.StartsWith("gpt-4", StringComparison.OrdinalIgnoreCase)) body["temperature"] = t1;
                endpoint = "https://api.openai.com/v1/responses";
                bearer = apiKey;
                break;
            case TokenHarbor:
            {
                // Chat Completions puro: o schema vai no prompt e a resposta passa pelo parser tolerante (cercas de código, texto extra).
                var content = prompt;
                if (schema is not null)
                    content += "\n\nRespond ONLY with one valid JSON object (no markdown, no commentary) that follows this JSON Schema:\n" + schema.ToJsonString(Json);
                body = new JsonObject
                {
                    ["model"] = model, ["max_tokens"] = maxOutputTokens,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
                };
                if (temperature is { } t2) body["temperature"] = t2;
                endpoint = TokenHarborEndpoint;
                bearer = apiKey;
                break;
            }
            case "gemini":
            {
                var config = new JsonObject { ["maxOutputTokens"] = maxOutputTokens };
                if (temperature is { } t3) config["temperature"] = t3;
                if (schema is not null)
                {
                    config["responseMimeType"] = "application/json";
                    if (useSchema) config["responseJsonSchema"] = Strictify(schema);
                }
                if (thinkingBudget is { } budget && model.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase))
                    config["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = budget };
                endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{WebUtility.UrlEncode(model)}:generateContent?key={WebUtility.UrlEncode(apiKey)}";
                body = new JsonObject
                {
                    ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
                    ["generationConfig"] = config,
                };
                break;
            }
            default:
                throw new ArgumentException("Provedor de IA invalido.");
        }

        string responseText;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json") };
            if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            responseText = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var bodyText = responseText.Length > 65536 ? responseText[..65536] : responseText;
                var quota = QuotaStatuses.Contains(code) || Text.Take(bodyText, 300).ToLowerInvariant().Contains("quota");
                Record(provider, model, null, false, quota);
                var detail = DetailFromBody(bodyText, code, provider, model, "Chamada de IA", apiKey);
                if (quota) throw new AiUnavailableException($"IA indisponível: {detail}") { HttpStatus = code };
                throw new AiException(detail) { HttpStatus = code };
            }
        }
        catch (Exception exc) when ((exc is HttpRequestException or IOException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            Record(provider, model, null, false, true);
            throw new AiUnavailableException($"IA indisponível: {Scrub(exc.Message, apiKey)}", exc) { Transient = true }; // timeout/queda: tenta outro modelo e repete
        }

        JsonObject? result;
        try { result = JsonNode.Parse(responseText) as JsonObject; }
        catch (JsonException exc)
        {
            Record(provider, model, null, false, false);
            throw new AiResponseException("IA indisponível: resposta do provedor não é JSON.", exc);
        }
        // O contador entende o formato Gemini/OpenAI; as respostas do TokenHarbor só contam como chamada.
        Record(provider, model, provider == TokenHarbor ? null : result, true, false);
        result ??= [];
        return provider switch
        {
            TokenHarbor => TokenHarborReply(result),
            "openai" => OpenAiReply(result),
            _ => GeminiReply(result),
        };
    }

    private static Reply TokenHarborReply(JsonObject result)
    {
        var choice = (result["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var content = (choice?["message"] as JsonObject)?["content"];
        var text = content is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>().Trim() : "";
        return new Reply(text, choice?["finish_reason"]?.ToString() == "length");
    }

    private static Reply OpenAiReply(JsonObject result)
    {
        var parts = new List<string>();
        foreach (var item in (result["output"] as JsonArray) ?? [])
            foreach (var part in ((item as JsonObject)?["content"] as JsonArray) ?? [])
                if (part is JsonObject p && p["type"]?.ToString() == "output_text") parts.Add(p["text"]?.ToString() ?? "");
        var incomplete = result["status"]?.ToString() == "incomplete" && (result["incomplete_details"] as JsonObject)?["reason"]?.ToString() == "max_output_tokens";
        return new Reply(string.Join("\n", parts).Trim(), incomplete);
    }

    private static Reply GeminiReply(JsonObject result)
    {
        var candidates = (result["candidates"] as JsonArray) ?? [];
        var parts = new List<string>();
        foreach (var item in candidates)
            foreach (var part in (((item as JsonObject)?["content"] as JsonObject)?["parts"] as JsonArray) ?? [])
                if (part is JsonObject p && !Odradek.Core.Text.Truthy(p["thought"])) parts.Add(p["text"]?.ToString() ?? "");
        var truncated = candidates.Any(c => (c as JsonObject)?["finishReason"]?.ToString() == "MAX_TOKENS");
        return new Reply(string.Join("\n", parts).Trim(), truncated);
    }

    private static string Scrub(string text, string apiKey) =>
        apiKey.Length == 0 ? text : text.Replace(apiKey, "[chave omitida]").Replace(WebUtility.UrlEncode(apiKey), "[chave omitida]");

    /// <summary>Mantém a explicação do provedor sem expor chave nem URL; não copia HTML arbitrário de proxies.</summary>
    internal static string DetailFromBody(string body, int code, string provider, string model, string operation, string apiKey)
    {
        JsonNode? payload = null;
        try { payload = JsonNode.Parse(body); } catch (JsonException) { }
        var error = (payload as JsonObject)?["error"];
        var parts = new List<string>();
        if (error is JsonObject fields)
        {
            foreach (var field in new[] { "status", "code", "param", "message" })
            {
                if (fields[field] is JsonValue value && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number && Odradek.Core.Text.Truthy(value))
                    parts.Add(Odradek.Core.Text.PyStrRaw(value));
            }
        }
        else if (error is JsonValue ev && ev.GetValueKind() == JsonValueKind.String)
            parts.Add(ev.GetValue<string>());
        var detail = parts.Count > 0 ? string.Join(" | ", parts) : "O provedor não retornou uma explicação JSON.";
        detail = Scrub(detail, apiKey);
        detail = Text.Take(WhitespaceRegex().Replace(detail, " ").Trim(), 700);
        return $"{operation}: {provider}/{model} (HTTP {code}): {detail}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^```(?:json)?\s*|\s*```$", RegexOptions.IgnoreCase)]
    private static partial Regex FenceRegex();

    internal static JsonObject ParseJsonObject(string text)
    {
        var cleaned = FenceRegex().Replace(text.Trim(), "").Trim();
        return JsonNode.Parse(cleaned) as JsonObject ?? throw new FormatException("a resposta não é um objeto JSON");
    }

    /// <summary>
    /// Uma chamada exigindo JSON conforme o schema. Repete para cobrir JSON inválido e saída truncada (dobrando o orçamento de tokens)
    /// e, no Gemini, o 400 de schema nativo não suportado; esgotado, levanta <see cref="AiResponseException"/> (a fila repete depois).
    /// </summary>
    private async Task<JsonObject> JsonOnceAsync(string prompt, JsonObject schema, string provider, string model, string apiKey, int maxOutputTokens,
        double temperature, int? thinkingBudget, int timeoutSeconds, CancellationToken ct)
    {
        var tokens = maxOutputTokens;
        var useSchema = true;
        var lastProblem = "resposta vazia";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Reply reply;
            try { reply = await RequestAsync(prompt, provider, model, apiKey, tokens, temperature, timeoutSeconds, schema, thinkingBudget, useSchema, ct); }
            catch (AiException exc) when (exc.HttpStatus == 400 && useSchema && provider.ToLowerInvariant().Trim() == "gemini")
            {
                useSchema = false; // alguns modelos Gemini rejeitam responseJsonSchema
                continue;
            }
            if (reply.Truncated)
            {
                lastProblem = $"saída truncada em {tokens} tokens";
                tokens = Math.Min(tokens * 2, 32768);
                continue;
            }
            try { return ParseJsonObject(reply.Text); }
            catch (Exception exc) when (exc is JsonException or FormatException) { lastProblem = $"JSON inválido ({exc.Message})"; }
        }
        throw new AiResponseException($"IA indisponível: resposta inutilizável do modelo ({lastProblem}).");
    }
}
