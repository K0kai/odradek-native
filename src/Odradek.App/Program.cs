using System.Diagnostics;
using Avalonia;
using Odradek.Core;
using Odradek.Core.Data;
using Odradek.Core.Engines;
using Odradek.LocalApi;

namespace Odradek.App;

internal static class Program
{
    /// <summary><c>ODRADEK_INSTANCE</c> dá outro nome à instância única: só para conferir uma cópia de teste ao lado do app instalado (com <c>PORT</c> e <c>ODRADEK_DATA_DIR</c> próprios).</summary>
    private static readonly string InstanceSuffix = (Environment.GetEnvironmentVariable("ODRADEK_INSTANCE") ?? "").Trim();
    private static readonly string ShowEventName = @"Local\Odradek.ShowWindow" + InstanceSuffix;
    private static readonly object LogLock = new();
    private static EventWaitHandle? _showEvent;
    private static Mutex? _instanceMutex; // mantido vivo (e preso) até o fim do processo

    public static LocalServer Server { get; private set; } = null!;
    /// <summary>Uma única instância do banco para a janela e os motores: o <c>QueueRuntime</c> identifica os itens em execução por ela.</summary>
    public static Database Database { get; } = new();
    private static readonly Lazy<OdradekEngines> LazyEngines = new(() => new OdradekEngines(Database, secret: Environment.GetEnvironmentVariable("ODRADEK_SMOKE") is null ? null : _ => "")); // teste de fumaça: sem segredos, nada vai à rede paga
    public static OdradekEngines Engines => LazyEngines.Value; // montado só quando alguém usa (fora do caminho da abertura da janela)
    private static readonly Lazy<string> QpidTokenValue = new(() => Odradek.Core.Api.QpidToken.Resolve());
    /// <summary>Token das rotas <c>/api/qpid/*</c> (o mesmo que o servidor local confere); o Qpid precisa guardar este valor.</summary>
    public static string QpidApiToken => QpidTokenValue.Value;
    public static bool EnginesCreated => LazyEngines.IsValueCreated;
    public static Task ServerStart { get; private set; } = Task.CompletedTask;
    /// <summary><c>null</c> quando a atualização automática está desligada (o app não foi instalado por <c>install.ps1</c>).</summary>
    public static Odradek.Core.Update.Updater? AutoUpdater { get; private set; }

    public static double ProcessAgeMs() => (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    [STAThread]
    public static int Main(string[] args)
    {
        Directory.CreateDirectory(Paths.DataDir);
        Log($"Iniciando (dados em {Paths.DataDir}).");

        // Instância única: quem decide é a porta fixa, como no app Python (a extensão e o Qpid precisam dela).
        // Segunda execução do app novo: o mutex nomeado responde na hora. (Sondar a porta custava ~3 s quando ela está livre,
        // porque o Windows demora a recusar a conexão.) A sonda só roda se a porta não puder ser aberta, para escolher a mensagem.
        _instanceMutex = new Mutex(true, @"Local\Odradek.Instance" + InstanceSuffix, out var firstInstance);
        if (!firstInstance)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing)) existing.Set(); // traz a janela da primeira para a frente
            return 0;
        }
        Log($"Main a {ProcessAgeMs():F0} ms do início do processo.");

        // Uma atualização já baixada e ainda não aplicada (o app saiu antes da troca): aplica agora, antes de abrir qualquer coisa. O script de troca
        // espera este processo terminar, troca as pastas e reabre o app; se a troca falhar ele reabre a versão antiga e nunca repete a tentativa.
        AutoUpdater = Odradek.Core.Update.Updater.FromEnvironment(Log);
        if (AutoUpdater?.Staged() is { } pending && AutoUpdater.LaunchApply(pending, Environment.ProcessId, restart: true, background: false, LocalServer.Port))
            return 0;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        // O app novo é o dono do esquema: cria o banco numa instalação nova e migra um jobs.db de versão antiga (inclusive o do app Python) antes
        // de qualquer leitura. É idempotente e leva poucos milissegundos num banco já atual.
        try
        {
            var migration = Stopwatch.StartNew();
            Database.Initialize();
            Log($"Banco pronto em {migration.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Log("Falha ao preparar o banco: " + ex);
            NativeMessage.Show("Não foi possível abrir o banco de dados em " + Paths.DbPath + ". Veja desktop.log na pasta de dados.");
            return 1;
        }

        // O servidor local sobe em paralelo com a janela, em outra thread: construir o Kestrel (JIT) na thread da tela atrasava a janela.
        // Subir já aqui também fecha a corrida em que uma segunda execução, logo depois, ainda enxergaria a porta livre.
        // As rotas da extensão e do Qpid usam o banco e os motores já montados (só criados quando uma rota os pede) e o token do Qpid (cofre).
        Server = new LocalServer(new ApiServices(Database, () => Engines.Collector, () => Engines.Assistant, () => QpidTokenValue.Value));
        ServerStart = Task.Run(async () =>
        {
            var timer = Stopwatch.StartNew();
            await Server.StartAsync();
            Log($"Servidor local no ar em {timer.ElapsedMilliseconds} ms ({ProcessAgeMs():F0} ms do início do processo).");
        });
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log("Falha fatal: " + ex);
            NativeMessage.Show("O Odradek encontrou um erro e vai fechar. Veja desktop.log na pasta de dados.");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseWin32().UseSkia().UseHarfBuzz().LogToTrace();

    /// <summary>Uma segunda execução avisa por aqui; a primeira mostra a janela.</summary>
    public static void WatchForShowRequests(Action onRequest)
    {
        var handle = _showEvent;
        if (handle is null) return;
        new Thread(() => { while (handle.WaitOne()) onRequest(); }) { IsBackground = true, Name = "show-window" }.Start();
    }

    public static void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo(Paths.DataDir) { UseShellExecute = true }); }
        catch (Exception ex) { Log("Não foi possível abrir a pasta de dados: " + ex.Message); }
    }

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                var file = Paths.LogPath;
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{Clock.NowIso()} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* log nunca derruba o app */ }
    }
}
