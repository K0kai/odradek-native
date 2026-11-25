using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odradek.Core.Update;

/// <summary>Uma versão já baixada, conferida e extraída ao lado da instalação, pronta para a troca.</summary>
public sealed record StagedUpdate(Version Version, string Dir);

/// <summary>
/// Atualização automática a partir de um feed (<c>latest.json</c> + zip): um endereço <c>https://</c> (as Releases do GitHub, no app
/// distribuído) ou uma pasta local/de rede (desenvolvimento). Este tipo não tem UI: procura a versão nova, baixa e confere o SHA-256, extrai em <c>&lt;instalação&gt;.staging</c> e, na hora certa, entrega a troca ao <c>apply-update.ps1</c>
/// (que espera o app sair, troca as pastas, reabre e desfaz tudo se o app novo não responder). Só se atualiza um app instalado:
/// quem não tem <c>update-source.txt</c> na pasta (build local, dist) nunca é trocado.
/// </summary>
public sealed class Updater
{
    public const string MarkerFile = "update-source.txt";
    public const string FeedEnv = "ODRADEK_UPDATE_FEED";
    public const string ManifestName = "latest.json";
    private const string ScriptResource = "apply-update.ps1";

    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    /// <summary>Segue redirecionamentos (o <c>releases/latest/download/</c> do GitHub redireciona para o arquivo). O tempo-limite é por pedido.</summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly Action<string> _log;
    private readonly HttpClient _http;

    public string InstallDir { get; }
    public string DataDir { get; }
    /// <summary>O endereço <c>https://</c> ou a pasta do feed, como veio da configuração.</summary>
    public string Feed { get; }
    public Version Current { get; }

    public string UpdatesDir => Path.Combine(DataDir, "updates");
    public string StagingDir => InstallDir + ".staging";
    private string StagedFile => Path.Combine(UpdatesDir, "staged.json");
    private string FailedFile => Path.Combine(UpdatesDir, "failed.txt");
    private string SeenFile => Path.Combine(UpdatesDir, "seen-version.txt");

    /// <param name="handler">Só para testes: troca a rede do feed <c>https://</c>.</param>
    public Updater(string installDir, string dataDir, string feed, Version current, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        InstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        DataDir = dataDir;
        Feed = feed;
        _http = handler is null ? SharedHttp : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Current = UpdateManifest.Normalize(current);
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// O atualizador deste processo, ou <c>null</c> se a atualização está desligada: sem <c>update-source.txt</c> na pasta do app
    /// (não é uma instalação), <c>ODRADEK_UPDATE=0</c>, ou teste automático (<c>ODRADEK_SMOKE</c>, <c>ODRADEK_SHOTS</c>).
    /// <c>ODRADEK_UPDATE_FEED</c> troca o feed gravado na instalação.
    /// </summary>
    public static Updater? FromEnvironment(Action<string>? log = null)
    {
        if ((Environment.GetEnvironmentVariable("ODRADEK_UPDATE") ?? "").Trim() == "0") return null;
        if (Environment.GetEnvironmentVariable("ODRADEK_SMOKE") is not null || Environment.GetEnvironmentVariable("ODRADEK_SHOTS") is not null) return null;
        var installDir = AppContext.BaseDirectory;
        var marker = Path.Combine(installDir, MarkerFile);
        if (!File.Exists(marker)) return null;
        string feed;
        try { feed = (Environment.GetEnvironmentVariable(FeedEnv) is { Length: > 0 } env ? env : File.ReadAllText(marker)).Trim(); }
        catch (IOException) { return null; }
        if (feed.Length == 0) return null;
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
        return new Updater(installDir, Paths.DataDir, feed, current, log);
    }

    // ---------------------------------------------------------------- estado em disco

    private HashSet<Version> Failed()
    {
        var set = new HashSet<Version>();
        try
        {
            if (File.Exists(FailedFile))
                foreach (var line in File.ReadAllLines(FailedFile))
                    if (Version.TryParse(line.Trim(), out var v)) set.Add(UpdateManifest.Normalize(v));
        }
        catch (IOException) { /* sem a lista: tenta de novo */ }
        return set;
    }

    private void ClearStaged()
    {
        try { File.Delete(StagedFile); } catch (IOException) { }
        try { if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A versão preparada, se ainda vale: é mais nova que a em uso, não falhou antes e a pasta está inteira. Se não vale, limpa.</summary>
    public StagedUpdate? Staged()
    {
        try
        {
            if (!File.Exists(StagedFile)) return null;
            var node = JsonNode.Parse(File.ReadAllText(StagedFile)) as JsonObject;
            if (node is not null && Version.TryParse(node["version"]?.GetValue<string>(), out var parsed))
            {
                var version = UpdateManifest.Normalize(parsed);
                var dir = StagingDir;
                if (version > Current && !Failed().Contains(version) && File.Exists(Path.Combine(dir, "Odradek.exe")) && File.Exists(Path.Combine(dir, "Odradek.dll")))
                    return new StagedUpdate(version, dir);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
        ClearStaged(); // obsoleta, quebrada ou já usada
        return null;
    }

    /// <summary>Apaga a sobra da troca anterior (<c>.old</c>) e a de uma extração interrompida. Nunca lança.</summary>
    public void CleanLeftovers()
    {
        try
        {
            var old = InstallDir + ".old";
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (!File.Exists(StagedFile) && Directory.Exists(StagingDir)) Directory.Delete(StagingDir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("Update: não consegui limpar sobras: " + ex.Message); }
    }

    /// <summary>
    /// Se esta execução é de uma versão diferente da última que rodou aqui, devolve a anterior (o app foi atualizado); em todo caso grava a atual.
    /// Na primeira execução (sem registro) devolve <c>null</c>.
    /// </summary>
    public string? ConsumeVersionChange()
    {
        try
        {
            Directory.CreateDirectory(UpdatesDir);
            var previous = File.Exists(SeenFile) ? File.ReadAllText(SeenFile).Trim() : "";
            File.WriteAllText(SeenFile, Current.ToString());
            return previous.Length > 0 && previous != Current.ToString() ? previous : null;
        }
        catch (IOException) { return null; }
    }

    // ---------------------------------------------------------------- feed

    /// <summary>Tem esquema (<c>algo://</c>): é um endereço, não uma pasta.</summary>
    private bool FeedIsUrl => Feed.Contains("://", StringComparison.Ordinal);

    /// <summary>
    /// O endereço de um arquivo do feed. Só <c>https://</c> vale: por <c>http://</c> alguém no caminho trocaria o manifesto e o pacote juntos,
    /// e o SHA-256 não protegeria nada.
    /// </summary>
    private Uri FeedUri(string file)
    {
        if (!Uri.TryCreate(Feed.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new NotSupportedException("o feed por endereço precisa ser https://");
        return new Uri(baseUri, Uri.EscapeDataString(file));
    }

    private async Task<HttpResponseMessage> GetAsync(string file, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUri(file));
        request.Headers.UserAgent.ParseAdd($"Odradek-Updater/{Current}");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try { response.EnsureSuccessStatusCode(); }
        catch { response.Dispose(); throw; }
        return response;
    }

    /// <summary>O texto do <c>latest.json</c>, ou <c>null</c> se a pasta não o tem.</summary>
    private async Task<string?> FetchManifestAsync(CancellationToken ct)
    {
        if (!FeedIsUrl)
        {
            var path = Path.Combine(Feed, ManifestName);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ManifestTimeout);
        using var response = await GetAsync(ManifestName, limit.Token).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
    }

    private async Task DownloadAsync(string file, string destination, CancellationToken ct)
    {
        if (!FeedIsUrl)
        {
            await using var source = new FileStream(Path.Combine(Feed, file), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await SaveAsync(source, destination, ct).ConfigureAwait(false);
            return;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DownloadTimeout);
        using var response = await GetAsync(file, limit.Token).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
        await SaveAsync(body, destination, limit.Token).ConfigureAwait(false);
    }

    private static async Task SaveAsync(Stream input, string destination, CancellationToken ct)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- procura e preparo

    /// <summary>O <c>latest.json</c> do feed, ou <c>null</c> (feed fora do ar, ausente ou inválido; o motivo vai para o log).</summary>
    public async Task<UpdateManifest?> ReadManifestAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await FetchManifestAsync(ct).ConfigureAwait(false);
            if (json is null) return null;
            if (UpdateManifest.TryParse(json, out var manifest, out var error)) return manifest;
            _log($"Update: {ManifestName} recusado ({error}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or NotSupportedException or OperationCanceledException)
        {
            _log("Update: feed inacessível: " + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// Procura uma versão mais nova que a em uso e, havendo, baixa, confere e extrai. Devolve a versão preparada (esta ou uma anterior ainda
    /// válida), ou <c>null</c>. Só o cancelamento pedido sai como exceção: qualquer falha (inclusive tempo-limite da rede) vai para o log e o
    /// próximo ciclo tenta de novo.
    /// </summary>
    public async Task<StagedUpdate?> CheckAndStageAsync(CancellationToken ct = default)
    {
        var staged = Staged();
        UpdateManifest? manifest = null;
        var replacing = false; // a partir daqui a pasta preparada anterior já não existe
        try
        {
            manifest = await ReadManifestAsync(ct).ConfigureAwait(false);
            if (manifest is null || manifest.Version <= Current || Failed().Contains(manifest.Version)) return staged;
            if (staged is not null && staged.Version >= manifest.Version) return staged;

            _log($"Update: versão {manifest.Version} encontrada no feed (em uso: {Current}); preparando.");
            Directory.CreateDirectory(UpdatesDir);
            var local = Path.Combine(UpdatesDir, manifest.File);
            try
            {
                await DownloadAsync(manifest.File, local, ct).ConfigureAwait(false);
                string hash;
                await using (var stream = File.OpenRead(local))
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                if (hash != manifest.Sha256) { _log($"Update: SHA-256 de {manifest.File} não confere; ignorado."); return staged; }

                replacing = true;
                ClearStaged();
                ZipFile.ExtractToDirectory(local, StagingDir);
                if (!File.Exists(Path.Combine(StagingDir, "Odradek.exe")) || !File.Exists(Path.Combine(StagingDir, "Odradek.dll")))
                {
                    _log("Update: o pacote não tem Odradek.exe/Odradek.dll; ignorado.");
                    ClearStaged();
                    return null;
                }
                File.Copy(Path.Combine(InstallDir, MarkerFile), Path.Combine(StagingDir, MarkerFile), true); // a versão nova continua se atualizando
                await File.WriteAllTextAsync(StagedFile, new JsonObject { ["version"] = manifest.Version.ToString(), ["dir"] = StagingDir }.ToJsonString(), ct).ConfigureAwait(false);
            }
            finally { try { File.Delete(local); } catch (IOException) { } }

            _log($"Update: versão {manifest.Version} pronta para instalar.");
            return Staged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log($"Update: falha ao preparar {manifest?.Version.ToString() ?? "a atualização"}: {ex.GetType().Name}: {ex.Message}");
            if (!replacing) return staged; // o download falhou: a versão já preparada continua valendo
            ClearStaged(); // pode ter ficado uma extração pela metade
            return null;
        }
    }

    // ---------------------------------------------------------------- troca

    /// <summary>
    /// Entrega a troca ao script auxiliar (em outro processo, porque a pasta em uso não pode ser trocada por quem roda dela). O script espera
    /// o processo <paramref name="processId"/> sair. <paramref name="restart"/>: reabre o app depois (e volta atrás se ele não responder);
    /// <paramref name="background"/>: reabre só na bandeja. Devolve <c>false</c> se não conseguiu nem iniciar o script.
    /// </summary>
    public bool LaunchApply(StagedUpdate update, int processId, bool restart, bool background, int port)
    {
        try
        {
            Directory.CreateDirectory(UpdatesDir);
            var script = Path.Combine(UpdatesDir, ScriptResource);
            using (var resource = typeof(Updater).Assembly.GetManifestResourceStream(ScriptResource) ?? throw new InvalidOperationException("script de atualização ausente"))
            using (var reader = new StreamReader(resource, Encoding.UTF8))
                File.WriteAllText(script, reader.ReadToEnd(), new UTF8Encoding(true)); // com BOM: o PowerShell 5 lê acentos como UTF-8

            // A pasta de trabalho do script NÃO pode ser a da instalação (o atalho do app a define assim e o filho a herdaria): o Windows
            // não renomeia uma pasta que é a pasta atual de algum processo.
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = UpdatesDir };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                "-ProcessId", processId.ToString(), "-InstallDir", InstallDir, "-StagingDir", update.Dir, "-DataDir", DataDir,
                "-Version", update.Version.ToString(), "-Port", port.ToString() })
                start.ArgumentList.Add(arg);
            if (restart) start.ArgumentList.Add("-Restart");
            if (background) start.ArgumentList.Add("-Background");
            Process.Start(start)?.Dispose();
            _log($"Update: troca para {update.Version} entregue ao script (restart={restart}, background={background}).");
            return true;
        }
        catch (Exception ex)
        {
            _log("Update: não consegui iniciar o script de troca: " + ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- ociosidade

    /// <summary>
    /// Pode reiniciar sem atrapalhar? Só com a janela escondida (app na bandeja), nada em execução (fila, coleta, backup) e sem requisição da
    /// extensão/Qpid nos últimos <paramref name="quiet"/>.
    /// </summary>
    public static bool IsIdle(bool windowVisible, bool queueRunning, bool collectorRunning, bool backupRunning, DateTime lastApiRequestUtc, DateTime nowUtc, TimeSpan quiet) =>
        !windowVisible && !queueRunning && !collectorRunning && !backupRunning && nowUtc - lastApiRequestUtc >= quiet;
}
