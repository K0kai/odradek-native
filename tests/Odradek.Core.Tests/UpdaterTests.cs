using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Odradek.Core.Update;

namespace Odradek.Core.Tests;

/// <summary>A atualização automática: o que o app aceita do feed, como prepara a pasta nova e quando o app conta como ocioso.</summary>
public sealed class UpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "odradek-update-" + Guid.NewGuid().ToString("N"));
    private string Install => Path.Combine(_root, "install");
    private string Data => Path.Combine(_root, "data");
    private string Feed => Path.Combine(_root, "feed");
    private readonly List<string> _log = [];

    public UpdaterTests()
    {
        Directory.CreateDirectory(Install);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Feed);
        File.WriteAllText(Path.Combine(Install, Updater.MarkerFile), Feed);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private Updater Make(string current = "1.0.0") => new(Install, Data, Feed, Version.Parse(current), _log.Add);

    /// <summary>Publica um zip no feed (com os arquivos pedidos) e o <c>latest.json</c>; devolve o caminho do zip.</summary>
    private string Publish(string version, string[]? files = null, string? sha = null, string? manifestFile = null)
    {
        files ??= ["Odradek.exe", "Odradek.dll"];
        var zip = Path.Combine(Feed, $"Odradek-{version}-win-x64.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var name in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("conteudo " + version);
            }
        var hash = sha ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)));
        File.WriteAllText(Path.Combine(Feed, "latest.json"), new JsonObject
        {
            ["version"] = version, ["file"] = manifestFile ?? Path.GetFileName(zip), ["sha256"] = hash, ["published_at"] = "2026-11-24T10:00:00Z",
        }.ToJsonString());
        return zip;
    }

    [Fact]
    public async Task Without_a_manifest_nothing_happens()
    {
        Assert.Null(await Make().CheckAndStageAsync());
        Assert.False(Directory.Exists(Make().StagingDir));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("0.9.0")]
    public async Task A_version_that_is_not_newer_is_ignored(string published)
    {
        Publish(published);
        Assert.Null(await Make("1.0.0").CheckAndStageAsync());
        Assert.False(Directory.Exists(Make().StagingDir));
    }

    [Fact]
    public async Task A_newer_version_is_downloaded_checked_and_extracted_next_to_the_install()
    {
        Publish("1.0.1");
        var updater = Make();
        var staged = await updater.CheckAndStageAsync();

        Assert.NotNull(staged);
        Assert.Equal(new Version(1, 0, 1, 0), staged!.Version);
        Assert.Equal(Install + ".staging", staged.Dir);
        Assert.True(File.Exists(Path.Combine(staged.Dir, "Odradek.exe")));
        Assert.True(File.Exists(Path.Combine(staged.Dir, Updater.MarkerFile)), "a versão nova precisa continuar se atualizando");
        Assert.Empty(Directory.GetFiles(updater.UpdatesDir, "*.zip")); // o zip baixado não fica
        Assert.Equal(staged, updater.Staged());
    }

    [Fact]
    public async Task A_wrong_sha256_is_never_staged()
    {
        Publish("1.0.1", sha: new string('a', 64));
        Assert.Null(await Make().CheckAndStageAsync());
        Assert.False(Directory.Exists(Make().StagingDir));
        Assert.Contains(_log, line => line.Contains("SHA-256"));
    }

    [Fact]
    public async Task A_package_without_the_app_is_rejected()
    {
        Publish("1.0.1", files: ["Odradek.exe"]);
        var updater = Make();
        Assert.Null(await updater.CheckAndStageAsync());
        Assert.Null(updater.Staged());
        Assert.False(Directory.Exists(updater.StagingDir));
    }

    [Theory]
    [InlineData("..\\evil.zip")]
    [InlineData("sub/evil.zip")]
    [InlineData("C:\\evil.zip")]
    [InlineData("evil.exe")]
    public async Task A_manifest_pointing_outside_the_feed_is_refused(string file)
    {
        Publish("1.0.1", manifestFile: file);
        Assert.Null(await Make().CheckAndStageAsync());
        Assert.False(Directory.Exists(Make().StagingDir));
    }

    [Fact]
    public async Task A_version_that_failed_before_is_not_tried_again()
    {
        Publish("1.0.1");
        var updater = Make();
        Directory.CreateDirectory(updater.UpdatesDir);
        File.WriteAllText(Path.Combine(updater.UpdatesDir, "failed.txt"), "1.0.1\n");
        Assert.Null(await updater.CheckAndStageAsync());
        Assert.False(Directory.Exists(updater.StagingDir));
    }

    [Fact]
    public async Task A_staged_version_survives_the_feed_going_away_and_is_replaced_by_a_newer_one()
    {
        Publish("1.0.1");
        var updater = Make();
        await updater.CheckAndStageAsync();

        File.Delete(Path.Combine(Feed, "latest.json")); // feed offline: a já preparada continua valendo
        Assert.Equal(new Version(1, 0, 1, 0), (await updater.CheckAndStageAsync())!.Version);

        Publish("1.0.2");
        var newer = await updater.CheckAndStageAsync();
        Assert.Equal(new Version(1, 0, 2, 0), newer!.Version);
        Assert.Equal("conteudo 1.0.2", File.ReadAllText(Path.Combine(newer.Dir, "Odradek.exe")));
    }

    [Fact]
    public async Task A_staged_version_that_is_no_longer_newer_is_cleaned_up()
    {
        Publish("1.0.1");
        await Make("1.0.0").CheckAndStageAsync();

        var afterUpgrade = Make("1.0.1"); // o app já roda a 1.0.1 (instalada por outro caminho)
        Assert.Null(afterUpgrade.Staged());
        Assert.False(Directory.Exists(afterUpgrade.StagingDir));
    }

    [Fact]
    public async Task The_failed_list_also_invalidates_an_already_staged_version()
    {
        Publish("1.0.1");
        var updater = Make();
        await updater.CheckAndStageAsync();
        File.WriteAllText(Path.Combine(updater.UpdatesDir, "failed.txt"), "1.0.1\n"); // o script marca assim quando a troca falha
        Assert.Null(updater.Staged());
    }

    [Fact]
    public void Leftovers_of_the_previous_swap_are_removed()
    {
        Directory.CreateDirectory(Install + ".old");
        File.WriteAllText(Path.Combine(Install + ".old", "x"), "x");
        Make().CleanLeftovers();
        Assert.False(Directory.Exists(Install + ".old"));
    }

    [Fact]
    public void The_version_change_is_reported_once()
    {
        Assert.Null(Make("1.0.0").ConsumeVersionChange()); // primeira execução: nada a dizer
        Assert.Null(Make("1.0.0").ConsumeVersionChange());
        Assert.Equal("1.0.0.0", Make("1.0.1").ConsumeVersionChange());
        Assert.Null(Make("1.0.1").ConsumeVersionChange());
    }

    [Theory]
    [InlineData("a=1.2.3", "1.2.3.0")]
    [InlineData("a=1.2.3.4", "1.2.3.4")]
    public void Versions_with_three_or_four_parts_compare_alike(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateManifest.Normalize(Version.Parse(text[2..])));

    [Fact]
    public void A_manifest_needs_a_valid_version_file_and_sha()
    {
        var sha = new string('b', 64);
        Assert.True(UpdateManifest.TryParse($$"""{"version":"2026.1124.1530","file":"Odradek-x.zip","sha256":"{{sha}}"}""", out var ok, out _));
        Assert.Equal(new Version(2026, 1124, 1530, 0), ok!.Version);
        Assert.False(UpdateManifest.TryParse($$"""{"version":"abc","file":"a.zip","sha256":"{{sha}}"}""", out _, out _));
        Assert.False(UpdateManifest.TryParse("""{"version":"1.0.1","file":"a.zip","sha256":"curto"}""", out _, out _));
        Assert.False(UpdateManifest.TryParse("não é json", out _, out _));
        Assert.False(UpdateManifest.TryParse("[]", out _, out _));
    }

    [Theory]
    [InlineData(false, false, false, false, 600, true)]  // tudo quieto: ocioso
    [InlineData(true, false, false, false, 600, false)]  // janela aberta
    [InlineData(false, true, false, false, 600, false)]  // item da fila rodando
    [InlineData(false, false, true, false, 600, false)]  // coleta rodando
    [InlineData(false, false, false, true, 600, false)]  // backup rodando
    [InlineData(false, false, false, false, 120, false)] // extensão falou há 2 min
    [InlineData(false, false, false, false, 300, true)]  // exatamente 5 min: já vale
    public void Idle_rule(bool window, bool queue, bool collector, bool backup, int secondsSinceRequest, bool expected)
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, Updater.IsIdle(window, queue, collector, backup, now.AddSeconds(-secondsSinceRequest), now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_server_that_never_got_a_request_counts_as_quiet()
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(Updater.IsIdle(false, false, false, false, DateTime.MinValue, now, TimeSpan.FromMinutes(5)));
    }
}
