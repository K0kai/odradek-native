using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Ai;
using Odradek.Core.Backup;
using Odradek.Core.Candidate;
using Odradek.Core.Collector;
using Odradek.Core.Data;
using Odradek.Core.Queue;

namespace Odradek.Core.Engines;

/// <summary>
/// Monta os motores do app sobre um banco: cliente de IA, fila com seus handlers, coletor, cotas, filtros, assistente e a rotina diária.
/// É o equivalente dos <c>_boot_*</c> do <c>app.py</c>; a janela só usa o que está exposto aqui.
/// </summary>
public sealed class OdradekEngines : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _scheduler;
    private Task? _backupLoop;

    public Database Db { get; }
    public HttpClient Http { get; }
    public AiClient Ai { get; }
    public QuotaService Quotas { get; }
    public TriageService Triage { get; }
    public ResumeAnalyzer Resumes { get; }
    public WorthReverify Worth { get; }
    public PortfolioSync Portfolio { get; }
    public QueueEngine Queue { get; }
    public JobCollector Collector { get; }
    public FilterGenerator Filters { get; }
    public Assistant Assistant { get; }
    public DailyScheduler Scheduler { get; }
    public BackupService Backup { get; }

    /// <param name="secret">Leitura de segredos (padrão: Cofre de Credenciais do Windows).</param>
    /// <param name="handler">Transporte HTTP (padrão: o do sistema); os testes injetam um falso.</param>
    /// <param name="backupUrl">Destino do backup (padrão: <c>DATABASE_URL_STANDBY</c> ou o segredo <c>backup_database_url</c>).</param>
    public OdradekEngines(Database db, Func<string, string>? secret = null, HttpMessageHandler? handler = null, QueueEngineOptions? queueOptions = null, Func<string>? backupUrl = null)
    {
        Db = db;
        secret ??= name => SecretStore.Get(name);
        Http = handler is null ? AiClient.CreateHttp() : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var settings = new SettingsStore(db);
        Ai = new AiClient(Http, new UsageTracker(db),
            () => (secret("tokenharbor"), SettingsActions.ParseModelList(settings.Get("tokenharbor_fallback_models"))));
        var apify = new ApifyClient(Http);
        Quotas = new QuotaService(db, apify, secret);
        Triage = new TriageService(db, Ai, secret);
        Resumes = new ResumeAnalyzer(db, Ai, secret);
        Worth = new WorthReverify(db, Http);
        Portfolio = new PortfolioSync(db, Http);
        Filters = new FilterGenerator(db, Ai, secret);
        Assistant = new Assistant(db, Ai, secret);
        Queue = new QueueEngine(db, new Dictionary<string, QueueHandler>
        {
            [QueueEngine.KindApply] = Triage.ApplyHandler,
            [QueueEngine.KindResume] = Resumes.Handler,
            [QueueEngine.KindWorthReverify] = Worth.Handler,
        }, queueOptions);
        var sources = new JobSources(db, apify, secret, Quotas.RefreshApifyQuietlyAsync);
        Collector = new JobCollector(db, sources.Providers(), ct => Portfolio.SyncNowAsync(false, ct));
        Backup = new BackupService(db, backupUrl ?? (() => { var env = (Environment.GetEnvironmentVariable(BackupService.StandbyEnv) ?? "").Trim(); if (env.Length > 0) return env; if ((Environment.GetEnvironmentVariable("ODRADEK_DATA_DIR") ?? "").Trim().Length > 0) return ""; /* banco de teste: nunca grava no backup real do cofre */ return secret(BackupService.UrlSecret).Trim(); }));
        Scheduler = new DailyScheduler(db, ct => Portfolio.SyncNowAsync(false, ct), () => Queue.PurgeTerminalOlderThan(DailyScheduler.RetentionDays));
    }

    /// <summary>Currículos analisados antes do perfil verificado (v2) são reanalisados em segundo plano.</summary>
    public static List<string> EnqueueMissingResumeProfiles(Database db)
    {
        List<(string Language, string? Version)> rows;
        using (var connection = db.Open())
            rows = connection.Query<(string, string?)>("SELECT language, profile_version FROM resumes WHERE analysis_status IN ('ok','reused') AND analysis_summary != ''").ToList();
        var pending = rows.Where(r => (r.Version ?? "") != ResumeProfile.Version).Select(r => r.Language).ToList();
        var queue = new QueueActions(db);
        foreach (var language in pending)
            queue.Enqueue(QueueActions.KindResume, [new("language", language), new("force", true), new("requested_at", Clock.NowIso())], $"resume:{language}");
        if (pending.Count > 0)
            new EventLogStore(db).Add("info", "resume", $"Perfil verificado ausente em {string.Join(", ", pending.Select(x => x.ToUpperInvariant()))}; reanálise enfileirada.");
        return pending;
    }

    /// <summary>Liga a fila, pede a reanálise dos perfis antigos e inicia a rotina diária.</summary>
    public void Start()
    {
        Queue.Start();
        try { EnqueueMissingResumeProfiles(Db); }
        catch (Exception ex) { new EventLogStore(Db).Add("warning", "resume", $"Falha ao enfileirar reanálise de perfil do currículo: {ex.Message}"); }
        _scheduler = Task.Run(() => Scheduler.RunAsync(_stop.Token));
        _backupLoop = Task.Run(() => Backup.RunLoopAsync(_stop.Token));
    }

    /// <summary>Para o coletor, a rotina diária e a fila (espera os itens em andamento terminarem ou serem interrompidos).</summary>
    public async Task StopAsync()
    {
        _stop.Cancel();
        Collector.Stop();
        if (_scheduler is not null) await _scheduler.ConfigureAwait(false);
        if (_backupLoop is not null) await _backupLoop.ConfigureAwait(false); // cancela uma cópia em andamento (a transação desfaz)
        await Collector.WhenIdle().ConfigureAwait(false);
        await Task.Run(Queue.Dispose).ConfigureAwait(false); // Stop espera os itens em andamento (até 5 s)
    }

    public void Dispose()
    {
        _stop.Dispose();
        Http.Dispose();
    }
}
