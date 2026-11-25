using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Odradek.Core.Extension;

/// <summary>
/// Um navegador instalado, visto como destino da extensão. <see cref="ExtensionsPage"/> vazio: a extensão não funciona nele.
/// <see cref="Tested"/> falso: é baseado no Chromium, mas o painel lateral que a extensão usa pode não abrir.
/// </summary>
public sealed record BrowserInfo(
    string Name, string Executable, string ExtensionsPage, string DeveloperModeLabel, string LoadButtonLabel, bool Tested = true, string Note = "")
{
    public bool Supported => ExtensionsPage.Length > 0;
}

/// <summary>Os navegadores instalados (os registrados em <c>Clients\StartMenuInternet</c> e o Opera nas pastas padrão), classificados pelo executável.</summary>
public static class Browsers
{
    /// <param name="Command">O <c>shell\open\command</c> registrado: o caminho do executável, com ou sem aspas e argumentos.</param>
    public sealed record Registration(string Name, string Command);

    private const string DeveloperMode = "Modo do desenvolvedor";
    private const string LoadUnpacked = "Carregar sem compactação";
    /// <summary>O Opera e o Opera GX só ganharam o painel lateral das extensões (<c>chrome.sidePanel</c>) na versão 136.</summary>
    public const int MinimumOperaVersion = 136;

    /// <summary>O caminho do executável de uma linha de comando registrada, ou vazio.</summary>
    public static string ExecutableOf(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : "";
        }
        var end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? "" : text[..(end + 4)];
    }

    /// <summary>O que o app sabe fazer com um navegador; <c>null</c> para o que não é um navegador que valha listar.</summary>
    /// <param name="versionOf">A versão do produto no executável (só consultada para o Opera); sem ela, ou se não der para ler, não se exige versão.</param>
    public static BrowserInfo? Classify(Registration registration, Func<string, Version?>? versionOf = null)
    {
        var executable = ExecutableOf(registration.Command);
        var file = executable[(executable.LastIndexOfAny(['\\', '/']) + 1)..].ToLowerInvariant();
        BrowserInfo Chromium(string page, string load = LoadUnpacked, bool tested = true, string note = "") =>
            new(registration.Name, executable, page, DeveloperMode, load, tested, note);
        return file switch
        {
            "chrome.exe" => Chromium("chrome://extensions"),
            "msedge.exe" => Chromium("edge://extensions", "Carregar sem pacote",
                note: "O Edge pode avisar ao abrir que há extensões em modo desenvolvedor; mantenha-as ativadas."),
            "brave.exe" => Chromium("brave://extensions"),
            "vivaldi.exe" => Chromium("vivaldi://extensions", tested: false),
            "opera.exe" => Opera(registration, executable, versionOf),
            // "launcher.exe" é um nome comum: só vale o que está na pasta do Opera
            "launcher.exe" when executable.Contains("opera", StringComparison.OrdinalIgnoreCase) => Opera(registration, executable, versionOf),
            "firefox.exe" => new(registration.Name, executable, "", "", "", false, "A extensão não funciona no Firefox. Use um navegador baseado no Chromium."),
            _ => null,
        };
    }

    /// <summary>Opera e Opera GX usam o mesmo nome de executável: quem os separa é a pasta ou o nome registrado.</summary>
    private static BrowserInfo Opera(Registration registration, string executable, Func<string, Version?>? versionOf)
    {
        var gx = executable.Contains("Opera GX", StringComparison.OrdinalIgnoreCase)
            || registration.Name.Replace(" ", "").Contains("OperaGX", StringComparison.OrdinalIgnoreCase);
        var name = gx ? "Opera GX" : "Opera";
        if (versionOf?.Invoke(executable) is { Major: > 0 } version && version.Major < MinimumOperaVersion)
            return new(name, executable, "", "", "", false,
                $"A versão {version.Major} é antiga demais para a extensão. Atualize para a {MinimumOperaVersion} ou mais nova.");
        return new(name, executable, "opera://extensions", DeveloperMode, LoadUnpacked, Tested: gx); // conferido no Opera GX 140; no Opera comum, não
    }

    /// <summary>
    /// Os encontrados, sem repetir o mesmo executável (o usuário e a máquina podem registrar o mesmo, e o Opera tem dois executáveis na mesma
    /// pasta); compatíveis primeiro.
    /// </summary>
    public static IReadOnlyList<BrowserInfo> From(IEnumerable<Registration> registrations, Func<string, Version?>? versionOf = null) =>
        registrations.Select(r => Classify(r, versionOf)).OfType<BrowserInfo>()
            .DistinctBy(b => b.Name.StartsWith("Opera", StringComparison.Ordinal) ? Folder(b.Executable) : b.Executable, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(b => b.Supported).ThenByDescending(b => b.Tested).ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static string Folder(string executable) => executable[..Math.Max(0, executable.LastIndexOfAny(['\\', '/']))];

    public static IReadOnlyList<BrowserInfo> Installed() =>
        OperatingSystem.IsWindows() ? From(ReadRegistry().Concat(DefaultOperaFolders()).Where(r => File.Exists(ExecutableOf(r.Command))), FileVersion) : [];

    /// <summary>Um executável que o usuário apontou (navegador instalado fora do lugar comum); <c>null</c> se o app não o reconhece.</summary>
    public static BrowserInfo? FromFile(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome", "msedge" => "Microsoft Edge", "brave" => "Brave", "vivaldi" => "Vivaldi", "firefox" => "Mozilla Firefox",
            var other => other,
        };
        return Classify(new Registration(name, $"\"{executable}\""), FileVersion);
    }

    /// <summary>O Opera e o Opera GX nem sempre se registram como navegador no Windows: procura também onde eles se instalam por padrão.</summary>
    private static IEnumerable<Registration> DefaultOperaFolders()
    {
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        foreach (var root in roots.Where(r => r.Length > 0))
            foreach (var folder in new[] { "Opera GX", "Opera" })
                foreach (var file in new[] { "opera.exe", "launcher.exe" })
                    yield return new Registration(folder, $"\"{Path.Combine(root, folder, file)}\"");
    }

    private static Version? FileVersion(string executable)
    {
        try { return System.Version.TryParse(FileVersionInfo.GetVersionInfo(executable).ProductVersion, out var version) ? version : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static List<Registration> ReadRegistry()
    {
        var found = new List<Registration>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                    if (clients is null) continue;
                    foreach (var id in clients.GetSubKeyNames())
                    {
                        using var client = clients.OpenSubKey(id);
                        using var command = client?.OpenSubKey(@"shell\open\command");
                        if (command?.GetValue(null) is string line)
                            found.Add(new Registration(client!.GetValue(null) as string is { Length: > 0 } name ? name : id, line));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        return found;
    }
}
