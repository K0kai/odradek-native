using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Odradek.LocalApi;

namespace Odradek.App;

public partial class App : Application
{
    public static bool Quitting { get; private set; }

    private MainWindow? _window;
    private TrayIcon? _tray;
    private UpdateCoordinator? _updates;
    private NativeMenuItem? _updateItem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var theme = Environment.GetEnvironmentVariable("ODRADEK_THEME")?.ToLowerInvariant(); // só para conferir o visual
        if (theme is "light" or "dark" or "dracula")
            RequestedThemeVariant = theme switch { "dark" => Avalonia.Styling.ThemeVariant.Dark, "dracula" => Palette.Dracula, _ => Avalonia.Styling.ThemeVariant.Light };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // fechar a janela só esconde; "Sair" encerra
            _window = new MainWindow();
            // --background (o reinício da atualização automática): sobe só na bandeja; a janela abre pelo ícone, como depois do X.
            if (desktop.Args?.Contains("--background") != true) desktop.MainWindow = _window;
            desktop.Exit += (_, _) => Quitting = true;
            _tray = BuildTray();
            _ = StartServerAsync();
            Program.WatchForShowRequests(() => Dispatcher.UIThread.Post(ShowWindow));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartServerAsync()
    {
        try
        {
            await Program.ServerStart; // já começou em Program.Main, em paralelo com a janela
            Dispatcher.UIThread.Post(() => _window?.ShowServerState($"●  Servidor local em {LocalServer.Host}:{LocalServer.Port}"));
        }
        catch (Exception ex)
        {
            Program.Log("Falha ao iniciar o servidor local: " + ex);
            // Sem a porta fixa a extensão e o Qpid não funcionam: explica o porquê e encerra, como o app Python.
            var state = await LocalServer.ProbeAsync();
            var message = state == LocalServer.PortState.Ours
                ? $"O Odradek já está rodando em {LocalServer.Url} (outra versão do app). Feche-o pela bandeja antes de abrir este."
                : $"A porta {LocalServer.Port} já está em uso por outro programa. A extensão e o Qpid precisam dela; " +
                  "feche o programa que a usa e abra o Odradek de novo.";
            Program.Log($"Porta {LocalServer.Port}: {state}.");
            await Task.Run(() => NativeMessage.Show(message));
            await Dispatcher.UIThread.InvokeAsync(() => QuitAsync());
            return;
        }
        StartEngines();
        StartUpdates();
        if (_window is not null) await _window.LoadDbSummaryAsync();
        if (_window is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { Args: { } args } && args.Contains("--extensao"))
            await _window.OpenExtensionSettingsAsync();
    }

    /// <summary>Atualização automática (só no app instalado): procura versões novas e aplica quando o app está ocioso.</summary>
    private void StartUpdates()
    {
        if (Program.AutoUpdater is not { } updater) return;
        Program.Log($"Atualização automática ligada (versão {updater.Current}, feed {updater.Feed}).");
        _updates = new UpdateCoordinator(updater,
            () => Dispatcher.UIThread.Invoke(() => _window?.IsVisible == true),
            async () => { await Dispatcher.UIThread.InvokeAsync(() => QuitAsync(restartForUpdate: true)); return Quitting; });
        _updates.Start();
        _window?.AttachUpdates(_updates);
        if (_updateItem is { } item)
        {
            item.IsVisible = true;
            _updates.Changed += () => Dispatcher.UIThread.Post(RenderUpdateItem);
            RenderUpdateItem();
        }
    }

    private void RenderUpdateItem()
    {
        if (_updateItem is null || _updates is null) return;
        _updateItem.Header = _updates.Staged is { } staged ? $"Atualizar agora ({staged.Version.ToString(3)})" : "Procurar atualizações";
    }

    /// <summary>Item da bandeja: com versão pronta, aplica (reabrindo com a janela); sem ela, procura e avisa o resultado.</summary>
    private async Task UpdateFromTrayAsync()
    {
        if (_updates is null) return;
        try
        {
            if (_updates.Staged is not null)
            {
                await QuitAsync(restartForUpdate: true, background: false);
                _updates.Revalidate();
                return;
            }
            Toast.Show("Procurando atualizações…");
            Toast.Show(UpdateCoordinator.Describe(await _updates.CheckNowAsync()));
        }
        catch (Exception ex)
        {
            Program.Log("Update: falha ao procurar pela bandeja: " + ex);
            Toast.Show("Não foi possível procurar atualizações.");
        }
    }

    /// <summary>Liga a fila de IA, a rotina diária e a reanálise de perfis antigos. Falha aqui não derruba a janela: vai para o log.</summary>
    private static void StartEngines() => _ = Task.Run(() =>
    {
        try { Program.Engines.Start(); Program.Log("Motores iniciados (fila de IA, rotina diária)."); }
        catch (Exception ex) { Program.Log("Falha ao iniciar os motores: " + ex); }
    });

    private TrayIcon BuildTray()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Odradek/Assets/odradek-256.png"));
        var open = new NativeMenuItem("Abrir Odradek");
        open.Click += (_, _) => ShowWindow();
        var backup = new NativeMenuItem("Fazer backup agora");
        backup.Click += (_, _) => _ = BackupNowAsync();
        var folder = new NativeMenuItem("Abrir pasta de dados");
        folder.Click += (_, _) => Program.OpenDataFolder();
        var quit = new NativeMenuItem("Sair");
        quit.Click += async (_, _) => await QuitAsync();
        // Só no app instalado (atualização automática ligada): StartUpdates o mostra e acerta o texto.
        _updateItem = new NativeMenuItem("Procurar atualizações") { IsVisible = false };
        _updateItem.Click += async (_, _) => await UpdateFromTrayAsync();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = Program.AutoUpdater is { } updater ? $"Odradek {updater.Current.ToString(3)}" : "Odradek",
            Menu = new NativeMenu { open, backup, folder, _updateItem, new NativeMenuItemSeparator(), quit },
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        return tray;
    }

    /// <summary>Item "Fazer backup agora" da bandeja: copia o banco para o Postgres de backup e avisa o resultado.</summary>
    private static async Task BackupNowAsync()
    {
        try
        {
            Program.Log("Backup na nuvem (manual)…");
            var result = await Program.Engines.Backup.RunAsync();
            Toast.Show(result switch
            {
                null => "Configure a URL do backup em Configurações.",
                { Ok: true } => $"Backup concluído ({result.Rows} linhas).",
                _ => $"Falha no backup: {result.Error}",
            });
        }
        catch (Exception ex)
        {
            Program.Log("Falha no backup manual: " + ex);
            Toast.Show("Falha no backup: " + ex.GetType().Name);
        }
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <param name="restartForUpdate">
    /// Saída pedida pela atualização automática: o script de troca reabre o app já atualizado (na bandeja). Sem isso, uma versão já preparada
    /// também é aplicada ao sair, mas sem reabrir. O script espera este processo terminar antes de mexer em qualquer pasta.
    /// </param>
    /// <param name="background">Só vale com <paramref name="restartForUpdate"/>: reabre o app só na bandeja (atualização automática) ou com a janela (botão Atualizar agora).</param>
    public async Task QuitAsync(bool restartForUpdate = false, bool background = true)
    {
        if (Quitting) return;
        var updater = Program.AutoUpdater;
        var staged = updater?.Staged();
        if (restartForUpdate && (updater is null || staged is null)) return; // nada a aplicar: não fecha o app à toa
        // Entrega a troca antes de parar tudo: se o script não subir, o app ainda não parou e segue funcionando.
        if (staged is not null && !updater!.LaunchApply(staged, Environment.ProcessId, restartForUpdate, background, LocalServer.Port) && restartForUpdate)
            return;
        Quitting = true;
        _updates?.Stop();
        if (_tray is not null) _tray.IsVisible = false;
        try { if (Program.EnginesCreated) await Program.Engines.StopAsync(); }
        catch (Exception ex) { Program.Log("Falha ao parar os motores: " + ex.Message); }
        try
        {
            // Com os motores parados o banco está quieto. Pula se o último backup tem menos de 30 min; espera no máximo 60 s.
            if (Program.EnginesCreated && await Program.Engines.Backup.BackupOnExitAsync() is { } backup)
                Program.Log(backup.Ok ? $"Backup ao sair: {backup.Rows} linhas." : "Backup ao sair falhou: " + backup.Error);
        }
        catch (Exception ex) { Program.Log("Falha no backup ao sair: " + ex.Message); }
        await Program.Server.StopAsync(); // o fim do servidor não perde nada: logs e uso de IA são gravados na hora
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
