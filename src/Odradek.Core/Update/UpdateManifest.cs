using System.Text.Json;

namespace Odradek.Core.Update;

/// <summary>
/// <c>latest.json</c> da pasta-feed: <c>{ "version", "file", "sha256", "published_at" }</c>. O nome do arquivo precisa ser só um nome
/// (sem pasta nem <c>..</c>) e o SHA-256 tem 64 dígitos hexadecimais; qualquer outra coisa é recusada.
/// </summary>
public sealed record UpdateManifest(Version Version, string File, string Sha256, string PublishedAt)
{
    /// <summary>Compara versões de 3 e de 4 partes sem surpresa (<c>1.0.1</c> e <c>1.0.1.0</c> são a mesma).</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public static bool TryParse(string json, out UpdateManifest? manifest, out string error)
    {
        manifest = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "o manifesto não é um objeto JSON"; return false; }
            string Text(string name) => doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

            if (!Version.TryParse(Text("version"), out var version)) { error = "versão ausente ou inválida"; return false; }
            var file = Text("file");
            if (file.Length == 0 || file != Path.GetFileName(file) || file.Contains("..") || file.IndexOfAny(['/', '\\', ':']) >= 0
                || !file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            { error = "nome de arquivo inválido"; return false; }
            var sha = Text("sha256").ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) { error = "sha256 inválido"; return false; }

            manifest = new UpdateManifest(Normalize(version), file, sha, Text("published_at"));
            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            error = "JSON inválido: " + ex.Message;
            return false;
        }
    }
}
