using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Odradek.Core.Actions;
using Odradek.Core.Data;

namespace Odradek.Core.Collector;

/// <summary>Fontes de vagas (<c>app.py:758-830, 2027-2137</c>). Cada fonte devolve as vagas no formato comum; as do Apify chegam lote a lote.</summary>
public sealed class JobSources(Database db, ApifyClient client, Func<string, string>? secret = null, Func<double, Task>? refreshApifyQuota = null,
    ApifyClient.Options? apifyOptions = null)
{
    public delegate IAsyncEnumerable<CollectedJob> Provider(CancellationToken stop);

    private readonly EventLogStore _log = new(db);
    private readonly SettingsStore _settings = new(db);
    private readonly Func<string, string> _secret = secret ?? (name => SecretStore.Get(name));

    /// <summary>As fontes que o ciclo conhece, pelo nome usado em <c>settings.sources</c>.</summary>
    public IReadOnlyDictionary<string, Provider> Providers() => new Dictionary<string, Provider>
    {
        ["remotive"] = Remotive, ["remoteok"] = RemoteOk, ["adzuna"] = Adzuna, ["apify"] = Apify,
    };

    private static string Str(JsonObject item, string key, string fallback = "") =>
        item.ContainsKey(key) ? (item[key] is null ? "" : Text.PyStrRaw(item[key])) : fallback;

    private static string? Posted(JsonObject item, string key) => item[key] is null or JsonObject or JsonArray ? null : Text.PyStrRaw(item[key]);

    private static async IAsyncEnumerable<CollectedJob> Yield(IEnumerable<CollectedJob> jobs)
    {
        foreach (var job in jobs) yield return job;
        await Task.CompletedTask;
    }

    /// <summary>Feed público do Remotive (categoria software-dev).</summary>
    public async IAsyncEnumerable<CollectedJob> Remotive([EnumeratorCancellation] CancellationToken stop)
    {
        var data = await client.FetchJsonAsync("https://remotive.com/api/remote-jobs?category=software-dev&limit=100", ct: stop).ConfigureAwait(false) as JsonObject;
        var jobs = new List<CollectedJob>();
        foreach (var item in (data?["jobs"] as JsonArray ?? []).OfType<JsonObject>())
            jobs.Add(new CollectedJob("Remotive", Str(item, "id"), Str(item, "title"), Str(item, "company_name"), Str(item, "candidate_required_location", "Remote"),
                Str(item, "description"), Str(item, "url"), Posted(item, "publication_date")));
        foreach (var job in jobs) yield return job;
    }

    /// <summary>Feed JSON do Remote OK (o primeiro item é aviso legal, sem id).</summary>
    public async IAsyncEnumerable<CollectedJob> RemoteOk([EnumeratorCancellation] CancellationToken stop)
    {
        var data = await client.FetchJsonAsync("https://remoteok.com/api", ct: stop).ConfigureAwait(false);
        var jobs = new List<CollectedJob>();
        foreach (var item in (data as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!Text.Truthy(item["id"])) continue;
            jobs.Add(new CollectedJob("Remote OK", Str(item, "id"), Str(item, "position"), Str(item, "company"), Str(item, "location", "Worldwide"),
                Str(item, "description"), Str(item, "url"), Posted(item, "date")));
        }
        foreach (var job in jobs) yield return job;
    }

    /// <summary>Adzuna nos países escolhidos; as credenciais vêm do cofre do Windows.</summary>
    public async IAsyncEnumerable<CollectedJob> Adzuna([EnumeratorCancellation] CancellationToken stop)
    {
        var (appId, appKey) = (_secret("adzuna_app_id"), _secret("adzuna_app_key"));
        if (appId.Length == 0 || appKey.Length == 0) throw CollectorException.Value("Configure Adzuna app_id e app_key no painel.");
        var cfg = _settings.All();
        var searches = LinkedInSearch.Terms(cfg.GetValueOrDefault("keywords")).Take(2).ToList();
        var countries = (cfg.GetValueOrDefault("adzuna_countries") ?? "br,us,gb,ca").Split(',').Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).ToList();
        var jobs = new List<CollectedJob>();
        foreach (var country in countries)
            foreach (var search in searches)
            {
                var query = $"https://api.adzuna.com/v1/api/jobs/{LinkedInSearch.QuotePlus(country)}/search/1?app_id={LinkedInSearch.QuotePlus(appId)}&app_key={LinkedInSearch.QuotePlus(appKey)}&results_per_page=50&what={LinkedInSearch.QuotePlus(search)}&content-type=application/json";
                var data = await client.FetchJsonAsync(query, ct: stop).ConfigureAwait(false) as JsonObject;
                foreach (var item in (data?["results"] as JsonArray ?? []).OfType<JsonObject>())
                    jobs.Add(new CollectedJob("Adzuna", Str(item, "id"), Str(item, "title"),
                        item["company"] is JsonObject company ? Str(company, "display_name") : "",
                        item["location"] is JsonObject location ? Str(location, "display_name") : "",
                        Str(item, "description"), Str(item, "redirect_url"), Posted(item, "created")));
            }
        foreach (var job in jobs) yield return job;
    }

    private void Log(string level, string message) => _log.Add(level, "apify", message);

    private static double MonthlyLimit(IReadOnlyDictionary<string, string> cfg) =>
        double.TryParse((cfg.GetValueOrDefault("apify_monthly_credit_limit_usd") ?? "5").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) ? limit : 5.0;

    private async Task RefreshQuotaQuietly(double minAge = 0)
    {
        if (refreshApifyQuota is null) return;
        try { await refreshApifyQuota(minAge).ConfigureAwait(false); } catch (Exception) { /* falha de cota nunca derruba a coleta */ }
    }

    /// <summary>Executa os lotes dos filtros ativos respeitando a parada e o teto mensal de créditos; cada lote sai assim que termina.</summary>
    public async IAsyncEnumerable<CollectedJob> Apify([EnumeratorCancellation] CancellationToken stop)
    {
        var token = _secret("apify_token");
        if (token.Length == 0) throw CollectorException.Value("Configure o token da Apify no painel.");
        var cfg = _settings.All();
        var limit = MonthlyLimit(cfg);
        if (limit <= 0) throw CollectorException.Value("Limite mensal de créditos Apify está em zero; a fonte não será consultada.");
        var (used, cycleLabel) = await client.MonthlyUsageAsync(token).ConfigureAwait(false);
        _settings.SetMany(new Dictionary<string, string> { ["apify_last_usage_usd"] = ApifyClient.FormatMoney(used, 4), ["apify_last_usage_cycle"] = cycleLabel });
        await RefreshQuotaQuietly().ConfigureAwait(false); // o painel mostra a cota desde o início da coleta
        if (used >= limit)
            throw CollectorException.Value($"Limite mensal de créditos Apify atingido ({ApifyClient.FormatMoney(used, 2)} USD de {ApifyClient.FormatMoney(limit, 2)} USD no ciclo {cycleLabel}).");

        var actors = ApifyActors.Load(cfg).Where(a => a["enabled"] is null || Text.Truthy(a["enabled"])).ToList();
        if (actors.Count == 0) throw CollectorException.Value("Nenhum actor Apify habilitado em apify_actors_json.");
        JsonObject? mainFilter = null, secondaryFilter = null;
        var scope = new List<ScopeItem>();
        var shareRaw = cfg.GetValueOrDefault("linkedin_main_share") ?? "";
        var mainShare = shareRaw.Length == 0 ? 70 : ApifyActors.PyInt(JsonValue.Create(shareRaw)) is { } share ? (int)share : 70;
        JsonObject? Secondary()
        {
            var secondary = LinkedInFilters.Load(cfg, "secondary", Log);
            return secondary is not null && (secondary["enabled"] is null || Text.Truthy(secondary["enabled"])) ? secondary : null;
        }
        var customUsesSearch = actors.Any(LinkedInSearch.CustomActorUsesSearchPlaceholders);
        if (actors.Any(a => Text.PyStr(a["input_mode"]) == "linkedin_search"))
        {
            mainFilter = LinkedInFilters.Load(cfg, "main", Log);
            if (mainFilter is null) Log("warning", "Sem filtro Principal: nenhum run LinkedIn será iniciado. Gere o filtro Principal no painel.");
            else
            {
                secondaryFilter = Secondary();
                scope = LinkedInSearch.SearchScope(cfg);
                var city = (cfg.GetValueOrDefault("candidate_city") ?? "").Trim();
                if (!scope.Any(item => item.Location == city) && (cfg.GetValueOrDefault("geo_eligibility_enabled") ?? "1") != "0")
                    Log("warning", "Cidade de residência não configurada no Perfil: a busca na sua cidade (híbrido/presencial) foi pulada.");
                Log("info", "Escopo das buscas: " + string.Join("; ", scope.Select(item => item.Label)) + ".");
                var preferred = LinkedInSearch.PreferredCountries(cfg);
                if (preferred.Count > 0) Log("info", "Países com viés (buscados primeiro): " + string.Join(", ", preferred) + ".");
                var phases = LinkedInSearch.RecencyPhases(LinkedInSearch.MaxAgeDays(cfg));
                Log("info", "Janela de publicação (mais recentes primeiro): " + string.Join(" → ", phases.Select(d => d == 1 ? "24h" : $"{d} dias")) + ".");
            }
        }
        else if (customUsesSearch)
        {
            mainFilter = LinkedInFilters.Load(cfg, "main", Log); // actors custom também buscam pelos cargos dos filtros
            if (mainFilter is not null) secondaryFilter = Secondary();
        }
        var customLocations = LinkedInSearch.CustomSearchLocations(cfg);
        if (customUsesSearch) Log("info", "Locais das buscas custom (Indeed etc.): " + string.Join(", ", customLocations) + ".");

        var results = new List<CollectedJob>();
        var errors = new List<string>();
        var planned = LinkedInSearch.PlanApifyRuns(actors, mainFilter, secondaryFilter, scope, mainShare, LinkedInSearch.MaxAgeDays(cfg), customLocations, LinkedInSearch.Terms(cfg.GetValueOrDefault("keywords")));
        var activeCount = new[] { mainFilter, secondaryFilter }.Count(f => f is not null);
        Log("info", $"Coleta planejada: Principal{(secondaryFilter is not null ? " + Secundário" : "")} ({activeCount} filtro(s)), {planned.Count} execução(ões) Apify.");
        var attempted = 0;
        foreach (var (runActor, linkedinFilter, linkedinUrls) in planned)
        {
            if (stop.IsCancellationRequested) break;
            var runLabel = Text.Truthy(runActor["label"]) ? Text.PyStrRaw(runActor["label"]) : Text.PyStrRaw(runActor["id"]);
            double usedNow; // uma checagem de uso que falha não pode iniciar mais lotes pagos às cegas
            try { (usedNow, _) = await client.MonthlyUsageAsync(token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                errors.Add($"{runLabel}: não foi possível verificar créditos ({ex.Message})");
                break;
            }
            if (usedNow >= limit)
            {
                errors.Add($"{runLabel}: limite de créditos atingido antes da execução");
                break;
            }
            attempted++;
            List<CollectedJob> batch;
            try
            {
                batch = await client.RunActorAsync(token, runActor, cfg, linkedinFilter, linkedinUrls, () => LinkedInFilters.Load(cfg, "main", Log), Log, RefreshQuotaQuietly, stop, apifyOptions).ConfigureAwait(false);
                if (linkedinFilter is not null)
                {
                    int dropped;
                    (batch, dropped) = ApifyItems.DropExcludedRegion(batch);
                    if (dropped > 0) Log("info", $"{runLabel}: {dropped} vaga(s) da Índia descartada(s) antes de entrar na coleta.");
                    var remoteSearch = Text.Truthy(runActor["_search_remote"]) ? "1" : "";
                    batch = batch.Select(job => job with { FilterId = Text.PyStrRaw(linkedinFilter["id"]), SearchRemote = remoteSearch }).ToList();
                }
                results.AddRange(batch);
            }
            catch (Exception ex)
            {
                errors.Add($"{runLabel}: {ex.Message}");
                Log("error", $"Falha no actor {runLabel}: {ex.Message}");
                await RefreshQuotaQuietly().ConfigureAwait(false);
                continue;
            }
            await RefreshQuotaQuietly().ConfigureAwait(false); // o run acabou de gastar créditos
            foreach (var job in batch) yield return job;
        }
        if (attempted < planned.Count)
            Log("warning", $"Cobertura incompleta: {attempted} de {planned.Count} execução(ões) iniciadas; {planned.Count - attempted} pendente(s).");
        try
        {
            var (usedAfter, cycleAfter) = await client.MonthlyUsageAsync(token).ConfigureAwait(false);
            _settings.SetMany(new Dictionary<string, string> { ["apify_last_usage_usd"] = ApifyClient.FormatMoney(usedAfter, 4), ["apify_last_usage_cycle"] = cycleAfter });
        }
        catch (Exception) { /* só o registro do uso; a coleta já terminou */ }
        if (errors.Count > 0 && results.Count == 0) throw CollectorException.Runtime("Todos os actors Apify falharam: " + string.Join("; ", errors));
        if (errors.Count > 0) Log("warning", "Alguns actors falharam: " + string.Join("; ", errors));
    }
}
