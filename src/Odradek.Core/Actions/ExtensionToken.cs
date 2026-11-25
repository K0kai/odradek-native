using System.Security.Cryptography;
using System.Text;
using Odradek.Core.Data;

namespace Odradek.Core.Actions;

/// <summary>Token de pareamento da extensão (<c>web/extension_token.py</c>): só o SHA-256 fica no banco; o token aparece uma vez.</summary>
public sealed class ExtensionTokenActions(Database db)
{
    public const string HashKey = "__extension_token_hash__";
    public const string CreatedKey = "__extension_token_created_at__";

    public static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public (bool Configured, string? CreatedAt) Status()
    {
        var store = new SettingsStore(db);
        var created = store.Get(CreatedKey);
        return (store.Get(HashKey).Length > 0, created.Length > 0 ? created : null);
    }

    /// <summary>"odk_" + 32 bytes aleatórios em base64 seguro para URL (43 caracteres), como o <c>secrets.token_urlsafe(32)</c>.</summary>
    public string Issue()
    {
        var token = "odk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        new SettingsStore(db).SetMany(new Dictionary<string, string> { [HashKey] = Digest(token), [CreatedKey] = Clock.NowIso() });
        new EventLogStore(db).Add("info", "extension", "Token de pareamento da extensão gerado.");
        return token;
    }

    public void Revoke()
    {
        new SettingsStore(db).SetMany(new Dictionary<string, string> { [HashKey] = "", [CreatedKey] = "" });
        new EventLogStore(db).Add("info", "extension", "Token de pareamento da extensão revogado.");
    }

    /// <summary>Comparação em tempo constante do token recebido com o hash guardado.</summary>
    public bool Verify(string supplied)
    {
        var stored = new SettingsStore(db).Get(HashKey);
        if (stored.Length == 0 || string.IsNullOrEmpty(supplied)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Digest(supplied)), Encoding.UTF8.GetBytes(stored));
    }
}
