using Odradek.Core.Data;
using Odradek.Core.Queue;
using Odradek.Core.Update;

namespace Odradek.App;

/// <summary>Resultado de uma procura de atualização pedida pelo usuário.</summary>
internal enum UpdateCheck { UpToDate, Ready, FeedUnavailable, Failed }

/// <summary>
/// Dirige a atualização automática: procura uma versão nova 2 min depois de abrir e a cada 30 min; com uma versão preparada, confere a cada
/// minuto se o app está ocioso (janela escondida, nada em execução, extensão/Qpid quietos há 5 min) e, estando, sai pelo caminho normal de
/// <see cref="App.QuitAsync"/> deixando o script de troca reabrir o app já atualizado, na bandeja. A tela e a bandeja também mostram a versão
/// preparada (<see cref="Staged"/>/<see cref="Changed"/>) e deixam o usuário procurar (<see cref="CheckNowAsync"/>) e aplicar na hora.
/// <c>ODRADEK_UPDATE_INTERVAL_SEC</c> troca todos os tempos acima por um só (para conferir o fluxo sem esperar).
/// </summary>
internal sealed class UpdateCoordinator
{
    private readonly Updater _updater;
    private readonly Func<bool> _windowVisible;
    private readonly Func<Task<bool>> _quitForUpdate;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1); // uma procura por vez (a automática e a do botão não se atropelam)
    private readonly TimeSpan _first, _checkEvery, _idleEvery, _quiet;

    public Updater Updater => _updater;

    /// <summary>A versão já baixada e pronta para instalar, ou <c>null</c>.</summary>
    public StagedUpdate? Staged { get; private set; }

    /// <summary>Disparado (em qualquer thread) quando <see cref="Staged"/> muda.</summary>
    public event Action? Changed;

    /// <param name="windowVisible">Lida na thread da tela.</param>
    /// <param name="quitForUpdate">Encerra o app para a troca; <c>false</c> se não encerrou (a versão preparada deixou de valer).</param>
    public UpdateCoordinator(Updater updater, Func<bool> windowVisible, Func<Task<bool>> quitForUpdate)
    {
        _updater = updater;
        _windowVisible = windowVisible;
        _quitForUpdate = quitForUpdate;
        TimeSpan? test = int.TryParse(Environment.GetEnvironmentVariable("ODRADEK_UPDATE_INTERVAL_SEC"), out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        _first = test ?? TimeSpan.FromMinutes(2);
        _checkEvery = test ?? TimeSpan.FromMinutes(30);
        _idleEvery = test ?? TimeSpan.FromMinutes(1);
        _quiet = test ?? TimeSpan.FromMinutes(5);
        Staged = updater.Staged(); // uma versão baixada numa sessão anterior já aparece no botão
    }

    public void Start() => _ = Task.Run(RunAsync);

    public void Stop() => _cts.Cancel();

    private void Set(StagedUpdate? staged)
    {
        if (Equals(Staged, staged)) return;
        Staged = staged;
        Changed?.Invoke();
    }

    /// <summary>Confere de novo se a versão preparada ainda vale (depois de uma troca que não aconteceu).</summary>
    public void Revalidate() => Set(_updater.Staged());

    /// <summary>Procura agora (o botão "Procurar atualizações"): baixa e prepara se houver versão mais nova.</summary>
    public async Task<UpdateCheck> CheckNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var staged = await _updater.CheckAndStageAsync(ct);
            Set(staged);
            if (staged is not null) return UpdateCheck.Ready;
            var manifest = _updater.ReadManifest();
            if (manifest is null) return UpdateCheck.FeedUnavailable;
            return manifest.Version > _updater.Current ? UpdateCheck.Failed : UpdateCheck.UpToDate; // há versão nova que não pôde ser preparada
        }
        finally { _gate.Release(); }
    }

    public static string Describe(UpdateCheck result) => result switch
    {
        UpdateCheck.UpToDate => "Você já está na versão mais recente.",
        UpdateCheck.Ready => "Há uma versão nova pronta: use Atualizar agora.",
        UpdateCheck.FeedUnavailable => "Não consegui acessar a pasta de atualizações.",
        _ => "Não foi possível preparar a atualização. Veja desktop.log na pasta de dados.",
    };

    private bool IdleNow() =>
        Updater.IsIdle(
            windowVisible: _windowVisible(),
            queueRunning: QueueRuntime.RunningIds(Program.Database).Count > 0,
            collectorRunning: Program.Engines.Collector.Snapshot().State is "running" or "stopping",
            backupRunning: Program.Engines.Backup.IsRunning,
            lastApiRequestUtc: Program.Server.LastRequestUtc,
            nowUtc: DateTime.UtcNow,
            quiet: _quiet);

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            _updater.CleanLeftovers();
            if (_updater.ConsumeVersionChange() is { } from)
            {
                var message = $"Odradek atualizado de {from} para {_updater.Current}.";
                Program.Log("Update: " + message);
                new EventLogStore(Program.Database).Add("info", "update", message);
            }

            await Task.Delay(_first, ct);
            var nextCheck = DateTime.UtcNow;
            using var timer = new PeriodicTimer(_idleEvery);
            do
            {
                if (DateTime.UtcNow >= nextCheck)
                {
                    await CheckNowAsync(ct);
                    nextCheck = DateTime.UtcNow + _checkEvery;
                }
                if (Staged is { } staged && !App.Quitting && IdleNow())
                {
                    Program.Log($"Update: app ocioso; reiniciando para a versão {staged.Version}.");
                    if (await _quitForUpdate()) return;
                    Revalidate(); // a versão preparada não vale mais: procura de novo no próximo ciclo
                    nextCheck = DateTime.UtcNow;
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { /* o app está saindo */ }
        catch (Exception ex) { Program.Log("Update: o coordenador parou: " + ex); }
    }
}
