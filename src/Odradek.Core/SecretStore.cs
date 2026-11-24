using System.Runtime.InteropServices;
using System.Text;

namespace Odradek.Core;

/// <summary>
/// Segredos (chaves de API, senha SMTP) no Cofre de Credenciais do Windows, compatíveis com o <c>keyring</c> do Python
/// (<c>WinVaultKeyring</c>): serviço <c>job-scraper</c>, blob em UTF-16LE, alvo <c>job-scraper</c> ou <c>nome@job-scraper</c>.
/// A leitura segue a ordem do app Python: variável de ambiente primeiro, depois o cofre.
/// </summary>
public static class SecretStore
{
    public const string Service = "job-scraper";
    private const uint CredTypeGeneric = 1;
    private const uint PersistEnterprise = 3; // o mesmo valor que o keyring grava
    private const int ErrorNotFound = 1168;

    private static readonly Dictionary<string, string[]> EnvVars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini"] = ["GEMINI_API_KEY", "GOOGLE_API_KEY"],
        ["openai"] = ["OPENAI_API_KEY"],
        ["tokenharbor"] = ["TOKENHARBOR_API_KEY"],
        ["adzuna_app_id"] = ["ADZUNA_APP_ID"],
        ["adzuna_app_key"] = ["ADZUNA_APP_KEY"],
        ["apify_token"] = ["APIFY_TOKEN", "APIFY_API_TOKEN"],
        ["smtp_password"] = ["SMTP_PASSWORD"],
    };

    public enum Origin { None, Environment, Vault }

    public static string Get(string name, string service = Service) => Lookup(name, service).Value;

    public static (string Value, Origin Origin) Lookup(string name, string service = Service)
    {
        if (EnvVars.TryGetValue(name, out var variables))
            foreach (var variable in variables)
            {
                var value = (Environment.GetEnvironmentVariable(variable) ?? "").Trim();
                if (value.Length > 0) return (value, Origin.Environment);
            }
        var stored = ReadVault(name, service);
        return stored.Length > 0 ? (stored, Origin.Vault) : ("", Origin.None);
    }

    public static bool Has(string name, string service = Service) => Lookup(name, service).Value.Length > 0;

    /// <summary>Grava no cofre como o <c>keyring.set_password</c>; valor vazio não faz nada.</summary>
    public static void Set(string name, string value, string service = Service)
    {
        value = value.Trim();
        if (value.Length == 0) return;
        RequireWindows();
        var existing = Read(service);
        if (existing is not null)
            Write($"{existing.Value.User}@{service}", existing.Value.User, existing.Value.Secret); // o valor atual vai para o alvo composto
        Write(service, name, value);
    }

    /// <summary>Remove o segredo (como o <c>keyring.delete_password</c>); devolve se havia algo.</summary>
    public static bool Delete(string name, string service = Service)
    {
        RequireWindows();
        var deleted = false;
        foreach (var target in new[] { service, $"{name}@{service}" })
        {
            var existing = Read(target);
            if (existing is null || existing.Value.User != name) continue;
            CredDelete(target, CredTypeGeneric, 0);
            deleted = true;
        }
        return deleted;
    }

    private static string ReadVault(string name, string service)
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            var plain = Read(service);
            if (plain is not null && plain.Value.User == name) return plain.Value.Secret;
            return Read($"{name}@{service}")?.Secret ?? "";
        }
        catch (Exception) { return ""; } // cofre indisponível: o app segue sem a chave, como o Python
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O cofre de credenciais só existe no Windows.");
    }

    private static (string User, string Secret)? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new System.ComponentModel.Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            if (bytes.Length > 0) Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return (credential.UserName ?? "", DecodeBlob(bytes));
        }
        finally { CredFree(pointer); }
    }

    // O pywin32 grava UTF-16LE sem BOM; aceita também um BOM e, em último caso, UTF-8 (como o DecodingCredential do keyring).
    private static string DecodeBlob(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length % 2 == 0) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void Write(string target, string user, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                Comment = "Stored using python-keyring", // igual ao do keyring, para os dois apps tratarem a entrada do mesmo jeito
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = PersistEnterprise,
            };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { handle.Free(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
