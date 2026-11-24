using System.Security.Cryptography;

namespace Odradek.Core.Api;

/// <summary>
/// Token de <c>/api/qpid/*</c> (<c>_ensure_qpid_token</c> de <c>desktop_app.py</c>): a variável <c>QPID_API_TOKEN</c>, senão o Cofre do Windows
/// (<c>job-scraper</c>/<c>qpid_api_token</c>, o mesmo do app Python), gerado uma vez; sem cofre, o arquivo <c>qpid_token.txt</c> na pasta de dados.
/// </summary>
public static class QpidToken
{
    public const string Name = "qpid_api_token";

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Resolve(string? dataDir = null, Func<string, string>? vaultGet = null, Action<string, string>? vaultSet = null, string? environment = null)
    {
        var fromEnvironment = (environment ?? Environment.GetEnvironmentVariable("QPID_API_TOKEN") ?? "").Trim();
        if (fromEnvironment.Length > 0) return fromEnvironment;
        try
        {
            var token = (vaultGet ?? (name => SecretStore.Get(name)))(Name);
            if (token.Length == 0)
            {
                token = NewToken();
                (vaultSet ?? ((name, value) => SecretStore.Set(name, value)))(Name, token);
            }
            return token;
        }
        catch (Exception)
        {
            var file = Path.Combine(dataDir ?? Paths.DataDir, "qpid_token.txt");
            try { if (File.ReadAllText(file).Trim() is { Length: > 0 } stored) return stored; } catch (IOException) { }
            var fresh = NewToken();
            File.WriteAllText(file, fresh);
            return fresh;
        }
    }
}
