using System.Text.RegularExpressions;
using Dapper;
using Odradek.Core.Actions;
using Odradek.Core.Data;

namespace Odradek.Core.Collector;

public sealed record CollectorSnapshot(string State, string Message, string? LastRun, int LastFound, string? NextRunAt, int? MaxApply);

/// <summary>
/// Um ciclo por clique (<c>app.py:2157-2313</c>): consulta as fontes, grava as vagas novas (sem repetir pelo fingerprint) e, com
/// <c>auto_apply</c> ligado, manda as novas para a fila de triagem. Uma nova coleta exige novo clique.
/// </summary>
public sealed partial class JobCollector
{
    private readonly Database _db;
    private readonly IReadOnlyDictionary<string, JobSources.Provider> _providers;
    private readonly Func<CancellationToken, Task>? _beforeCycle;
    private readonly Func<int?, string> _evaluateNew;
    private readonly EventLogStore _log;
    private readonly object _lock = new();
    private CancellationTokenSource? _stop;
    private Task? _task;
    private string _state = "stopped";
    private string _message = "Coleta parada";
    private string? _lastRun;
    private int _lastFound;
    private string? _nextRunAt;
    private int? _maxApply;

    /// <param name="beforeCycle">Roda antes de ler as configurações (sincroniza o portfólio: o snapshot novo vale já neste ciclo). Nunca derruba o ciclo.</param>
    /// <param name="evaluateNew">Triagem das vagas novas; por padrão a ação "Triar vagas novas".</param>
    public JobCollector(Database db, IReadOnlyDictionary<string, JobSources.Provider> providers, Func<CancellationToken, Task>? beforeCycle = null, Func<int?, string>? evaluateNew = null)
    {
        _db = db;
        _providers = providers;
        _beforeCycle = beforeCycle;
        _evaluateNew = evaluateNew ?? (limit => new QueueActions(db).EvaluateNew(limit));
        _log = new EventLogStore(db);
    }

    public CollectorSnapshot Snapshot()
    {
        lock (_lock) return new CollectorSnapshot(_state, _message, _lastRun, _lastFound, _nextRunAt, _maxApply);
    }

    /// <summary>Espera o ciclo atual terminar (testes e encerramento do app).</summary>
    public Task WhenIdle() => _task ?? Task.CompletedTask;

    private void Publish() => _db.Bus.Publish(new CollectorChanged());

    public bool Start(int? maxApply = null)
    {
        lock (_lock)
        {
            if (_task is { IsCompleted: false }) return false;
            _maxApply = maxApply is > 0 ? maxApply : null;
            _stop = new CancellationTokenSource();
            _state = "running";
            _message = "Iniciando busca…";
            _nextRunAt = null;
            var stop = _stop.Token;
            _task = Task.Run(() => LoopAsync(stop));
        }
        _log.Add("success", "collector", "Bot de coleta iniciado.");
        Publish();
        return true;
    }

    private void SetProgress(string message, int? found = null)
    {
        lock (_lock)
        {
            _message = message;
            if (found is { } value) _lastFound = value;
        }
        Publish(); // a mensagem de progresso aparece no painel na hora
    }

    public void Stop()
    {
        var announce = false;
        lock (_lock)
        {
            _stop?.Cancel();
            if (_state == "running")
            {
                _state = "stopping";
                _message = "Parando após a busca atual…";
                announce = true;
            }
        }
        if (!announce) return;
        _log.Add("info", "collector", "Parada solicitada; aguardando fim da busca atual.");
        Publish();
    }

    private static string ErrorName(Exception ex) => ex switch
    {
        CollectorException c => c.PythonName,
        ActionException => "ValueError",
        _ => ex.GetType().Name.Replace("Exception", "Error"),
    };

    private async Task LoopAsync(CancellationToken stop)
    {
        try
        {
            lock (_lock) _nextRunAt = null;
            await RunOnceAsync(stop).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetProgress($"Coleta encerrada com erro: {ErrorName(ex)}.");
            _log.Add("error", "collector", $"Falha no ciclo: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _state = "stopped";
                _nextRunAt = null;
                if (stop.IsCancellationRequested) _message = "Coleta interrompida. Clique em Iniciar para executar outra vez.";
                else if (!_message.Contains("erro", StringComparison.OrdinalIgnoreCase)) _message += " Clique em Iniciar para executar outra vez.";
            }
            _log.Add("info", "collector", "Ciclo encerrado; nenhuma nova busca foi agendada.");
            Publish();
        }
    }

    /// <summary>Termo inteiro: "qa" não casa com "aqua" e "java" não casa com "javascript".</summary>
    public static bool MatchesPreferences(CollectedJob job, IReadOnlyList<string> keywordTerms)
    {
        if (job.FilterId.Length > 0) return true; // filtros do LinkedIn substituem as preferências gerais
        if (keywordTerms.Count == 0) return true;
        var searchable = string.Join(' ', job.Title, job.Company, job.Location, Text.CleanJobHtml(job.Description)).ToLowerInvariant();
        return keywordTerms.Any(term => term.Length > 0 && Regex.IsMatch(searchable, @"(?<![^\W_])" + Regex.Escape(term) + @"(?![^\W_])"));
    }

    private async Task RunOnceAsync(CancellationToken stop)
    {
        var started = Clock.NowIso();
        _log.Add("info", "collector", "Início do ciclo de busca.");
        long runId;
        using (var connection = _db.Open())
            runId = connection.ExecuteScalar<long>("INSERT INTO runs(started_at,state) VALUES(@started, 'running') RETURNING id", new { started });
        if (_beforeCycle is not null)
            try { await _beforeCycle(stop).ConfigureAwait(false); } catch (Exception) { /* a sincronização do portfólio nunca derruba o ciclo */ }
        var cfg = new SettingsStore(_db).All();
        var keywordTerms = LinkedInSearch.Terms(cfg.GetValueOrDefault("keywords"));
        var sources = (cfg.GetValueOrDefault("sources") ?? "").Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
        var found = 0;
        var limitReached = false;
        var errors = new List<string>();
        var maxApply = _maxApply;
        SetProgress("Buscando vagas…", 0);
        foreach (var source in sources)
        {
            if (stop.IsCancellationRequested || limitReached) break;
            if (!_providers.TryGetValue(source, out var provider))
            {
                errors.Add($"Fonte desconhecida: {source}");
                continue;
            }
            SetProgress($"Aguardando vagas de {source}… {found} vaga(s) nova(s) salva(s) nesta execução.", found);
            _log.Add("info", "collector", $"Consultando fonte {source}…");
            try
            {
                var received = 0;
                await foreach (var job in provider(stop).ConfigureAwait(false)) // sair do laço fecha o gerador: nenhum lote pago novo começa
                {
                    received++;
                    if (stop.IsCancellationRequested) break;
                    if (maxApply is { } cap && found >= cap) { limitReached = true; break; }
                    if (!MatchesPreferences(job, keywordTerms)) continue;
                    if (job.Url.Length == 0 || job.Title.Length == 0) continue;
                    if (JobStore.Insert(_db, job) is not { } id) continue;
                    found++;
                    _db.Bus.Publish(new JobsChanged([id]));
                    SetProgress($"Consultando {source}… {found} vaga(s) nova(s) salva(s) nesta execução.", found);
                    if (maxApply is { } limit && found >= limit) { limitReached = true; break; }
                }
                _log.Add("info", "collector", $"{source}: {received} anúncio(s) recebidos antes dos filtros.");
            }
            catch (Exception ex) // uma fonte fora do ar não impede as outras
            {
                errors.Add($"{source}: {ErrorName(ex)}");
                _log.Add("error", "collector", $"Falha ao consultar {source}: {ex.Message}");
            }
        }
        var applyNote = "";
        if (!stop.IsCancellationRequested && (cfg.GetValueOrDefault("auto_apply") ?? "") == "1")
        {
            SetProgress($"Triagem/candidatura automática… {found} vaga(s) nova(s).", found);
            _log.Add("info", "auto-apply", "Iniciando triagem/candidatura automática do ciclo.");
            applyNote = _evaluateNew(maxApply);
            _log.Add("info", "auto-apply", applyNote.Length > 0 ? applyNote : "Batch concluído sem mensagens.");
        }
        else if (!stop.IsCancellationRequested) _log.Add("info", "auto-apply", "auto_apply desligado; triagem automática ignorada neste ciclo.");
        var finished = Clock.NowIso();
        var message = $"Busca concluída: {found} vaga(s) nova(s)."
            + (limitReached ? $" Limite de {maxApply} atingido; coleta encerrada." : "")
            + (errors.Count > 0 ? " Avisos: " + string.Join("; ", errors) : "");
        if (applyNote.Length > 0) message += $" Auto-apply: {applyNote}";
        _log.Add(errors.Count == 0 ? "success" : "warning", "collector", message);
        using (var connection = _db.Open())
            connection.Execute("UPDATE runs SET finished_at=@finished,state=@state,found_count=@found,message=@message WHERE id=@runId",
                new { finished, state = stop.IsCancellationRequested ? "stopped" : "completed", found, message, runId });
        lock (_lock) (_lastRun, _lastFound, _message) = (finished, found, message);
    }
}
