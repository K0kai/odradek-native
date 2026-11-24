using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Odradek.Core.Data;

namespace Odradek.Core.Candidate;

/// <summary>Falha ao buscar ou validar o portfólio (a mensagem aparece no painel).</summary>
public sealed class PortfolioException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Resultado de uma sincronização (<c>status</c>: disabled, unchanged, updated ou error).</summary>
public sealed record PortfolioSyncResult(string Status, string Version, string Message);

/// <summary>
/// Portfólio público (<c>/ai/profile.json</c>) como segunda fonte de evidência (porte de <c>candidate/portfolio_source.py</c>).
/// Sem IA: o endpoint já traz <c>facts</c> estruturados, então o perfil sai no formato v2 de <see cref="ResumeProfile.GroundProfile"/>.
/// </summary>
public static partial class PortfolioSource
{
    public const string BuilderVersion = "1";
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    public const int MaxRecords = 500;
    public const string UserAgent = "job-scraper-portfolio-sync/1";

    private static readonly Dictionary<string, string> KindToCategory = new()
    {
        ["language"] = "language", ["framework"] = "framework", ["tool"] = "tool", ["practice"] = "domain", ["ai_tool"] = "tool",
    };

    [GeneratedRegex(@"^(?<name>.+?)\s*\((?<level>[^)]*)\)\s*$", RegexOptions.Singleline)]
    private static partial Regex LevelRegex();

    // ------------------------------------------------------------ busca e validação

    private static string S(JsonNode? node) => Text.PyStr(node);
    private static bool NonEmptyString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0;

    public static JsonObject ValidatePayload(JsonNode? payload)
    {
        if (payload is not JsonObject obj) throw new PortfolioException("Resposta do portfólio não é um objeto JSON.");
        // isinstance(schema, int) em Python também aceita bool (True == 1)
        long schema = 0;
        var schemaOk = obj["schema"] is JsonValue sv
            && (sv.TryGetValue<bool>(out var flag) ? (schema = flag ? 1 : 0) >= 1 : sv.TryGetValue<long>(out schema) && !LooksFloat(sv) && schema >= 1);
        if (!schemaOk) throw new PortfolioException("Resposta do portfólio sem `schema` válido.");
        if (!NonEmptyString(obj["version"])) throw new PortfolioException("Resposta do portfólio sem `version`.");
        if (obj["records"] is not JsonArray records || records.Count == 0) throw new PortfolioException("Resposta do portfólio sem registros.");
        var clean = new JsonArray();
        foreach (var item in records.Take(MaxRecords))
        {
            if (item is not JsonObject record) continue;
            if (!new[] { "id", "kind", "language", "text" }.All(k => NonEmptyString(record[k]))) continue;
            if (record["facts"] is not JsonObject) continue;
            clean.Add(record.DeepClone());
        }
        if (clean.Count == 0)
            throw new PortfolioException("O portfólio não traz `facts` estruturados nos registros: atualize o endpoint /ai/profile.json.");
        return new JsonObject { ["schema"] = schema, ["version"] = obj["version"]!.DeepClone(), ["records"] = clean };
    }

    private static bool LooksFloat(JsonValue value) => value.ToJsonString().AsSpan().IndexOfAny('.', 'e', 'E') >= 0;

    public static async Task<JsonObject> FetchAsync(HttpClient http, string url, int timeoutSeconds = 20, CancellationToken ct = default)
    {
        url = (url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new PortfolioException("URL do portfólio inválida: use http(s)://...");
        var data = await FetchJsonAsync(http, uri, timeoutSeconds, ct);
        // Quem cola o endereço da página (ex.: https://site/pt) recebe HTML: tenta o endpoint padrão na raiz do mesmo site.
        if (data is null && !uri.AbsolutePath.Equals(ProfilePath, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = new UriBuilder(uri) { Path = ProfilePath, Query = "", Fragment = "" }.Uri;
            data = await FetchJsonAsync(http, fallback, timeoutSeconds, ct);
        }
        if (data is null)
            throw new PortfolioException($"O portfólio não retornou JSON válido. Use o endereço do endpoint, como {new Uri(uri, ProfilePath)}.");
        return ValidatePayload(data);
    }

    /// <summary>Caminho padrão do endpoint de perfil do portfólio.</summary>
    public const string ProfilePath = "/ai/profile.json";

    /// <summary>Baixa e interpreta a URL; <c>null</c> quando o corpo não é JSON (HTML, texto, bytes inválidos).</summary>
    private static async Task<JsonNode?> FetchJsonAsync(HttpClient http, Uri uri, int timeoutSeconds, CancellationToken ct)
    {
        byte[] body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBodyBytes) break;
            }
            body = buffer.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new PortfolioException($"Não foi possível buscar o portfólio: {ex.Message}", ex);
        }
        if (body.Length > MaxBodyBytes) throw new PortfolioException("Resposta do portfólio maior que 2 MB.");
        try { return JsonNode.Parse(new UTF8Encoding(false, true).GetString(body).TrimStart('﻿')); } // BOM não é JSON válido
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { return null; }
    }

    // ------------------------------------------------------------ montagem do perfil

    /// <summary>Recorta em limite de palavra, para o trecho continuar sendo substring do texto original.</summary>
    private static string Clip(JsonNode? value, int limit) => Clip(S(value), limit);

    private static string Clip(string raw, int limit)
    {
        var text = Squash(raw);
        if (Text.Take(text, limit).Length == text.Length) return text;
        var head = Text.Take(text, limit);
        var cut = head.LastIndexOf(' ');
        return (cut > 0 ? head[..cut] : head).Trim();
    }

    private static string Squash(string raw) => string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string End(JsonNode? value) => Text.Truthy(value) ? Text.PyStrRaw(value) : "present";

    private static List<string> StrList(JsonNode? value, int limit = 40) =>
        value is JsonArray array
            ? array.Where(i => Text.PyStrRaw(i).Trim().Length > 0).Select(i => Squash(Text.PyStrRaw(i))).Take(limit).ToList()
            : [];

    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());

    /// <summary>Perfil no formato v2 a partir dos registros do idioma. Fatos levam <c>source</c> e <c>url</c>.</summary>
    public static JsonObject BuildProfile(JsonObject payload, string language)
    {
        var records = ((JsonArray)payload["records"]!).OfType<JsonObject>().Where(r => S(r["language"]) == language).ToList();
        var skills = new JsonArray();
        var roles = new JsonArray();
        var projects = new JsonArray();
        var education = new JsonArray();
        var languages = new JsonArray();
        var profile = new JsonObject
        {
            ["version"] = ResumeProfile.Version, ["source"] = "portfolio", ["headline"] = "", ["professional_profile"] = "", ["seniority"] = "",
            ["location_notes"] = "", ["work_authorization_notes"] = "", ["roles"] = roles, ["skills"] = skills, ["transferable_skills"] = new JsonArray(),
            ["education"] = education, ["certifications"] = new JsonArray(), ["projects"] = projects, ["achievements"] = new JsonArray(), ["languages"] = languages,
        };
        var skillByKey = new HashSet<string>();

        void AddSkill(string name, string category, string quote)
        {
            name = Text.Take(Squash(name), 80);
            var canonical = Text.NormalizeText(name);
            if (name.Length == 0 || canonical.Length == 0 || !skillByKey.Add(canonical)) return;
            skills.Add(new JsonObject
            {
                ["id"] = $"S{skills.Count + 1}", ["name"] = name, ["canonical"] = canonical, ["category"] = category,
                ["quotes"] = Strings([quote]), ["source"] = "portfolio",
            });
        }

        var stackSources = new List<(List<string> Stack, string Quote)>();
        foreach (var record in records)
        {
            var kind = S(record["kind"]);
            var facts = (JsonObject)record["facts"]!;
            var url = record["url"] is { } u ? u.DeepClone() : JsonValue.Create("");
            var text = Clip(record["text"], 4000);
            switch (kind)
            {
                case "profile":
                    profile["professional_profile"] = text;
                    profile["location_notes"] = Clip(facts["residence"], 200); // residência, não cidadania nem autorização
                    break;
                case "skills":
                    var items = StrList(facts["items"], 80);
                    if (S(facts["kind"]) == "spoken")
                    {
                        foreach (var entry in items)
                        {
                            var match = LevelRegex().Match(entry);
                            var (name, level) = match.Success ? (match.Groups["name"].Value.Trim(), match.Groups["level"].Value.Trim()) : (entry, "");
                            languages.Add(new JsonObject
                            {
                                ["id"] = $"L{languages.Count + 1}", ["language"] = Text.Take(name, 60), ["level"] = Text.Take(level, 60),
                                ["quote"] = Text.Take(entry, 300), ["source"] = "portfolio", ["url"] = url.DeepClone(),
                            });
                        }
                    }
                    else
                    {
                        var category = KindToCategory.GetValueOrDefault(S(facts["kind"]), "other");
                        foreach (var entry in items) AddSkill(entry, category, Clip(text, 300));
                    }
                    break;
                case "experience":
                {
                    var roleId = $"R{roles.Count + 1}";
                    var responsibilities = new JsonArray();
                    var n = 1;
                    foreach (var bullet in StrList(facts["bullets"], 30))
                    {
                        responsibilities.Add(new JsonObject { ["id"] = $"{roleId}.{n++}", ["text"] = Clip(bullet, 400), ["quote"] = Clip(bullet, 400) });
                    }
                    var stack = StrList(facts["stack"]);
                    roles.Add(new JsonObject
                    {
                        ["id"] = roleId, ["title"] = Clip(facts["role"], 160), ["employer"] = Clip(facts["company"], 160),
                        ["start"] = S(facts["start"]), ["end"] = End(facts["end"]), ["quote"] = Clip(text, 200),
                        ["responsibilities"] = responsibilities, ["skills_used"] = Strings(stack), ["source"] = "portfolio", ["url"] = url.DeepClone(),
                    });
                    stackSources.Add((stack, Clip(text, 300)));
                    break;
                }
                case "project" when S(record["id"]).Count(c => c == ':') == 2: // lang:project:slug; ignora :bullet:N e :story
                {
                    var stack = StrList(facts["stack"]);
                    projects.Add(new JsonObject
                    {
                        ["id"] = $"P{projects.Count + 1}", ["text"] = Clip(text, 400), ["quote"] = Clip(text, 200),
                        ["title"] = Clip(Text.Truthy(facts["title"]) ? facts["title"] : record["title"], 160), ["slug"] = S(facts["slug"]),
                        ["headline"] = Clip(facts["headline"], 240), ["start"] = S(facts["start"]), ["end"] = End(facts["end"]),
                        ["stack"] = Strings(stack), ["source"] = "portfolio", ["url"] = url.DeepClone(),
                    });
                    stackSources.Add((stack, Clip(text, 300)));
                    break;
                }
                case "education":
                    education.Add(new JsonObject
                    {
                        ["id"] = $"E{education.Count + 1}", ["text"] = Clip(text, 400), ["quote"] = Clip(text, 200),
                        ["start"] = S(facts["start"]), ["end"] = End(facts["end"]), ["source"] = "portfolio", ["url"] = url.DeepClone(),
                    });
                    break;
            }
        }
        // itens de stack que não aparecem nas categorias de skills viram skill "other"
        foreach (var (stack, quote) in stackSources)
            foreach (var name in stack) AddSkill(name, "other", quote);
        return profile;
    }

    /// <summary>Texto de todos os registros (PT e EN) para verificar citações de qualquer idioma.</summary>
    public static string PortfolioText(JsonObject payload) =>
        string.Join('\n', ((JsonArray)payload["records"]!).OfType<JsonObject>().Select(r => Clip(r["text"], 4000)));

    // ------------------------------------------------------------ snapshot no banco

    /// <summary>Baixa o portfólio se a <c>version</c> mudou. Nunca lança; em falha mantém o último snapshot bom.</summary>
    public static async Task<PortfolioSyncResult> SyncAsync(Database db, string url, Func<string, CancellationToken, Task<JsonObject>> fetch,
        bool force = false, string? nowIso = null, CancellationToken ct = default)
    {
        var settings = new SettingsStore(db);
        url = (url ?? "").Trim();
        if (url.Length == 0) return new("disabled", settings.Get("portfolio_version"), "URL do portfólio não configurada.");
        JsonObject snapshot;
        string key;
        JsonObject payload;
        try
        {
            payload = await fetch(url, ct);
            key = $"{S(payload["version"])}+b{BuilderVersion}";
            if (!force && key == settings.Get("portfolio_version") && settings.Get("portfolio_profile_json").Length > 0)
            {
                settings.Set("portfolio_error", "");
                return new("unchanged", key, "Portfólio já está atualizado.");
            }
            snapshot = new JsonObject { ["pt"] = BuildProfile(payload, "pt"), ["en"] = BuildProfile(payload, "en") };
            if (!snapshot.Select(kv => (JsonObject)kv.Value!).Any(p => new[] { "roles", "projects", "skills" }.Any(k => ((JsonArray)p[k]!).Count > 0)))
                throw new PortfolioException("O portfólio não trouxe cargos, projetos nem skills.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = Text.Take(ex.Message, 500);
            if (message.Length == 0) message = ex.GetType().Name;
            settings.Set("portfolio_error", message);
            return new("error", settings.Get("portfolio_version"), message);
        }
        settings.SetMany(new Dictionary<string, string>
        {
            ["portfolio_profile_json"] = snapshot.ToJsonString(Json.Options),
            ["portfolio_text"] = PortfolioText(payload),
            ["portfolio_version"] = key,
            ["portfolio_synced_at"] = nowIso ?? Clock.NowIso(),
            ["portfolio_error"] = "",
        });
        var pt = (JsonObject)snapshot["pt"]!;
        int Count(string k) => ((JsonArray)pt[k]!).Count;
        return new("updated", key, $"Portfólio sincronizado: {Count("roles")} cargo(s), {Count("projects")} projeto(s), {Count("skills")} skill(s).");
    }

    /// <summary>(perfil do idioma, texto) do último snapshot salvo, ou <c>null</c>.</summary>
    public static (JsonObject Profile, string Text)? Load(Database db, string language = "pt")
    {
        var settings = new SettingsStore(db);
        var raw = settings.Get("portfolio_profile_json");
        if (raw.Length == 0) return null;
        JsonObject? snapshot;
        try { snapshot = JsonNode.Parse(raw) as JsonObject; }
        catch (JsonException) { return null; }
        if (snapshot is null) return null;
        var profile = (snapshot[language] ?? snapshot["pt"] ?? snapshot["en"]) as JsonObject;
        if (profile is null || S(profile["version"]) != ResumeProfile.Version) return null;
        return ((JsonObject)profile.DeepClone(), settings.Get("portfolio_text"));
    }

    public static JsonObject Status(Database db)
    {
        var settings = new SettingsStore(db);
        var profile = Load(db)?.Profile ?? [];
        var counts = new JsonObject();
        foreach (var key in new[] { "roles", "projects", "skills", "education", "languages" })
            counts[key] = (profile[key] as JsonArray)?.Count ?? 0;
        return new JsonObject
        {
            ["url"] = settings.Get("portfolio_profile_url"), ["version"] = settings.Get("portfolio_version"),
            ["synced_at"] = settings.Get("portfolio_synced_at"), ["error"] = settings.Get("portfolio_error"), ["counts"] = counts,
        };
    }
}
