using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odradek.Core.Extension;

/// <summary>
/// A cópia da extensão que o navegador carrega. Fica na pasta de dados, e não na do app, porque a atualização automática troca a pasta do app
/// inteira e o navegador perderia a extensão. O app a iguala à que veio no pacote a cada abertura.
/// </summary>
public sealed class ExtensionFolder(string source, string target, Action<string>? log = null)
{
    private const string Manifest = "manifest.json";

    private readonly object _gate = new();

    /// <summary>A pasta que o usuário carrega no navegador.</summary>
    public string Target { get; } = target;

    public static ExtensionFolder Default(Action<string>? log = null) =>
        new(Path.Combine(AppContext.BaseDirectory, "extension"), Path.Combine(Paths.DataDir, "extension"), log);

    /// <summary>A versão da extensão na pasta fixa, ou <c>null</c> se ainda não há uma. A extensão aberta compara com a dela e se recarrega.</summary>
    public string? Version()
    {
        try
        {
            var path = Path.Combine(Target, Manifest);
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject manifest ? manifest["version"]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Deixa a pasta fixa igual à do pacote, gravando por cima (o navegador pode estar com ela aberta) e o <c>manifest.json</c> por último:
    /// a versão nova só aparece com os outros arquivos no lugar. Devolve <c>true</c> se a pasta está pronta para carregar. Nunca lança.
    /// </summary>
    public bool Sync()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path.Combine(source, Manifest)))
                {
                    log?.Invoke("Extensão: o pacote não tem a pasta da extensão.");
                    return File.Exists(Path.Combine(Target, Manifest));
                }
                var wanted = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(source, f))
                    .OrderBy(name => name.Equals(Manifest, StringComparison.OrdinalIgnoreCase)).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
                var changed = 0;
                foreach (var name in wanted)
                {
                    var from = Path.Combine(source, name);
                    var to = Path.Combine(Target, name);
                    if (File.Exists(to) && File.ReadAllBytes(from).AsSpan().SequenceEqual(File.ReadAllBytes(to))) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to, overwrite: true);
                    changed++;
                }
                var keep = wanted.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(Target, "*", SearchOption.AllDirectories))
                    if (!keep.Contains(Path.GetRelativePath(Target, file))) { File.Delete(file); changed++; }
                if (changed > 0) log?.Invoke($"Extensão: pasta atualizada ({changed} arquivo(s), versão {Version()}).");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("Extensão: não consegui atualizar a pasta: " + ex.Message);
                return false;
            }
        }
    }
}
