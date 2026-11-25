using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Odradek.Core.Actions;
using Odradek.Core.Extension;

namespace Odradek.Core.Tests;

/// <summary>A extensão do navegador: o ID fixo, a pasta que o navegador carrega e os navegadores que o app oferece.</summary>
public sealed class ExtensionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "odradek-ext-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "pacote");
    private string Target => Path.Combine(_root, "dados", "extension");

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private void Package(string version, string script = "console.log(1)")
    {
        Directory.CreateDirectory(Path.Combine(Source, "icons"));
        File.WriteAllText(Path.Combine(Source, "manifest.json"), $$"""{"manifest_version":3,"version":"{{version}}"}""");
        File.WriteAllText(Path.Combine(Source, "panel.js"), script);
        File.WriteAllText(Path.Combine(Source, "icons", "a.svg"), "<svg/>");
    }

    [Fact]
    public void The_folder_is_created_and_then_follows_the_package()
    {
        Package("1.0.0");
        var folder = new ExtensionFolder(Source, Target);
        Assert.Null(folder.Version());
        Assert.True(folder.Sync());
        Assert.Equal("1.0.0", folder.Version());
        Assert.True(File.Exists(Path.Combine(Target, "icons", "a.svg")));

        File.Delete(Path.Combine(Source, "icons", "a.svg"));
        Package("1.0.1", "console.log(2)");
        File.Delete(Path.Combine(Source, "icons", "a.svg"));
        File.WriteAllText(Path.Combine(Source, "novo.js"), "x");
        Assert.True(folder.Sync());
        Assert.Equal("1.0.1", folder.Version());
        Assert.Equal("console.log(2)", File.ReadAllText(Path.Combine(Target, "panel.js")));
        Assert.True(File.Exists(Path.Combine(Target, "novo.js")));
        Assert.False(File.Exists(Path.Combine(Target, "icons", "a.svg")));
    }

    [Fact]
    public void An_unchanged_folder_is_left_alone()
    {
        Package("1.0.0");
        var folder = new ExtensionFolder(Source, Target);
        folder.Sync();
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(Target, "panel.js"), stamp);
        Assert.True(folder.Sync());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(Target, "panel.js")));
    }

    [Fact]
    public void A_missing_package_does_not_throw_or_erase_the_folder()
    {
        Package("1.0.0");
        new ExtensionFolder(Source, Target).Sync();
        var broken = new ExtensionFolder(Path.Combine(_root, "nada"), Target);
        Assert.True(broken.Sync()); // a pasta anterior continua carregável
        Assert.Equal("1.0.0", broken.Version());
        Assert.False(new ExtensionFolder(Path.Combine(_root, "nada"), Path.Combine(_root, "vazio")).Sync());
    }

    private static string Repository()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Odradek.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("raiz do repositório não encontrada");
    }

    /// <summary>O navegador deriva o ID da chave: SHA-256 dela, 32 primeiros dígitos hexadecimais, com 0-f trocados por a-p.</summary>
    [Fact]
    public void The_manifest_key_gives_the_id_the_app_trusts()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Repository(), "extension", "manifest.json")))!;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(manifest["key"]!.GetValue<string>())));
        var id = new string(hash[..32].Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());
        Assert.Equal(ExtensionIdentity.Id, id);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"", "chrome://extensions", true)]
    [InlineData("\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1", "edge://extensions", true)]
    [InlineData("C:\\Program Files\\BraveSoftware\\Brave-Browser\\Application\\brave.exe", "brave://extensions", true)]
    [InlineData("\"C:\\Users\\ana\\AppData\\Local\\Programs\\Opera\\launcher.exe\"", "opera://extensions", false)]
    [InlineData("\"C:\\Users\\ana\\AppData\\Local\\Vivaldi\\Application\\vivaldi.exe\"", "vivaldi://extensions", false)]
    public void Chromium_browsers_are_recognized_by_their_executable(string command, string page, bool tested)
    {
        var browser = Browsers.Classify(new Browsers.Registration("Navegador", command))!;
        Assert.True(browser.Supported);
        Assert.Equal(page, browser.ExtensionsPage);
        Assert.Equal(tested, browser.Tested);
        Assert.EndsWith(".exe", browser.Executable);
        Assert.DoesNotContain("\"", browser.Executable);
    }

    [Theory]
    [InlineData("C:\\Program Files\\Internet Explorer\\iexplore.exe")]
    [InlineData("\"C:\\Jogos\\launcher.exe\"")] // "launcher.exe" só vale se for o do Opera
    [InlineData("")]
    public void Other_programs_are_not_listed(string command) => Assert.Null(Browsers.Classify(new Browsers.Registration("x", command)));

    [Theory]
    [InlineData("Opera GXStable", "\"C:\\Users\\ana\\AppData\\Local\\Programs\\Opera GX\\opera.exe\"", "Opera GX")]
    [InlineData("Opera GX", "\"D:\\Programs\\Opera GX\\launcher.exe\"", "Opera GX")]
    [InlineData("OperaStable", "\"C:\\Users\\ana\\AppData\\Local\\Programs\\Opera\\opera.exe\"", "Opera")]
    public void Opera_and_Opera_GX_are_told_apart(string registered, string command, string name)
    {
        var browser = Browsers.Classify(new Browsers.Registration(registered, command), _ => new Version(136, 0, 6008, 50))!;
        Assert.Equal(name, browser.Name);
        Assert.True(browser.Supported);
        Assert.Equal("opera://extensions", browser.ExtensionsPage);
        Assert.Equal(name == "Opera GX", browser.Tested); // o Opera comum ainda mostra o aviso de não testado
    }

    [Theory]
    [InlineData(114, false)]
    [InlineData(135, false)]
    [InlineData(136, true)]
    [InlineData(140, true)]
    public void An_old_Opera_is_asked_to_update(int major, bool supported)
    {
        var browser = Browsers.Classify(new("Opera GX", "\"D:\\Programs\\Opera GX\\opera.exe\""), _ => new Version(major, 0, 1, 1))!;
        Assert.Equal(supported, browser.Supported);
        if (!supported) Assert.Contains("136", browser.Note);
    }

    [Fact]
    public void The_two_executables_of_one_Opera_folder_are_one_browser()
    {
        var list = Browsers.From(
        [
            new("Opera GX", "\"C:\\Opera GX\\opera.exe\""),
            new("Opera GX", "\"C:\\Opera GX\\launcher.exe\""),
            new("Opera", "\"C:\\Opera\\opera.exe\""),
        ]);
        Assert.Equal(["Opera GX", "Opera"], list.Select(b => b.Name)); // o testado vem antes
    }

    [Fact]
    public void Firefox_is_listed_as_unsupported_and_repeats_are_merged()
    {
        var list = Browsers.From(
        [
            new("Mozilla Firefox", "\"C:\\Firefox\\firefox.exe\""),
            new("Google Chrome", "\"C:\\Chrome\\chrome.exe\""),
            new("Google Chrome", "\"c:\\chrome\\CHROME.EXE\""),
        ]);
        Assert.Equal(["Google Chrome", "Mozilla Firefox"], list.Select(b => b.Name));
        Assert.False(list[1].Supported);
    }
}

/// <summary>"Conectar ao Odradek": quem pode pedir o token e o que o usuário decide.</summary>
public sealed class PairingTests
{
    private static async Task<(HttpStatusCode Status, JsonNode Json)> Pair(ApiHarness api, string? origin = ExtensionIdentity.Origin)
    {
        var request = api.Request("POST", "/api/extension/pair", token: null);
        if (origin is not null) request.Headers.Add("Origin", origin);
        var response = await api.Client.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://site.example")]
    [InlineData("http://127.0.0.1:8765")]
    [InlineData("null")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Anyone_but_the_extension_is_refused_without_asking_the_user(string? origin)
    {
        var asked = 0;
        using var api = ApiHarness.Start(confirmPairing: _ => { asked++; return Task.FromResult(true); });
        var (status, json) = await Pair(api, origin);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Null(json["token"]);
        Assert.Equal(0, asked);
        Assert.False(new ExtensionTokenActions(api.Temp.Db).Status().Configured);
    }

    [Fact]
    public async Task An_allowed_request_gets_a_token_that_works()
    {
        using var api = ApiHarness.Start(confirmPairing: _ => Task.FromResult(true));
        var (status, json) = await Pair(api);
        Assert.Equal(HttpStatusCode.OK, status);
        var token = json["token"]!.GetValue<string>();
        Assert.True(new ExtensionTokenActions(api.Temp.Db).Verify(token));
        var (profile, _, _) = await api.Send("GET", "/api/autofill-profile", token);
        Assert.Equal(HttpStatusCode.OK, profile);
    }

    [Fact]
    public async Task A_refused_request_keeps_the_previous_token()
    {
        using var api = ApiHarness.Start(confirmPairing: _ => Task.FromResult(false));
        var previous = new ExtensionTokenActions(api.Temp.Db).Issue();
        var (status, json) = await Pair(api);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Null(json["token"]);
        Assert.True(new ExtensionTokenActions(api.Temp.Db).Verify(previous));
    }

    [Fact]
    public async Task Only_one_question_is_open_at_a_time()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api = ApiHarness.Start(confirmPairing: _ => { shown.TrySetResult(); return answer.Task; });
        var first = Pair(api);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, (await Pair(api)).Status);
        answer.SetResult(false);
        Assert.Equal(HttpStatusCode.Forbidden, (await first).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Pair(api)).Status); // a vez foi liberada: pergunta de novo
    }

    [Fact]
    public async Task An_unanswered_question_expires_and_is_closed()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api = ApiHarness.Start(pairingTimeout: TimeSpan.FromMilliseconds(200), confirmPairing: ct =>
        {
            ct.Register(() => closed.TrySetResult());
            return new TaskCompletionSource<bool>().Task;
        });
        Assert.Equal(HttpStatusCode.RequestTimeout, (await Pair(api)).Status);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(new ExtensionTokenActions(api.Temp.Db).Status().Configured);
    }

    [Fact]
    public async Task Without_a_screen_to_ask_there_is_no_pairing()
    {
        using var api = ApiHarness.Start();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Pair(api)).Status);
    }

    [Fact]
    public async Task Health_reports_the_extension_version_when_there_is_one()
    {
        using var api = ApiHarness.Start(extensionVersion: () => "2026.1125.1900");
        var (_, json) = await api.Json("GET", "/health", token: null);
        Assert.Equal("2026.1125.1900", json!["extensionVersion"]!.GetValue<string>());
        Assert.Equal("odradek-scraper", json["service"]!.GetValue<string>());
    }
}
