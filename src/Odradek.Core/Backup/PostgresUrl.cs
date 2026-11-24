using Npgsql;

namespace Odradek.Core.Backup;

/// <summary>Erro com mensagem própria para o usuário (nunca traz host, usuário nem senha da URL).</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Converte a URL <c>postgresql://usuario:senha@host:porta/banco?sslmode=require</c> (o formato que Supabase e Neon entregam) em
/// parâmetros do Npgsql, que não lê esse formato sozinho. O <c>sslmode</c> segue o libpq: <c>require</c> cifra sem validar o certificado, <c>verify-ca</c> e <c>verify-full</c> validam.
/// </summary>
public static class PostgresUrl
{
    public static bool IsPostgres(string? spec) =>
        spec is not null && (spec.Trim().StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || spec.Trim().StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase));

    public static NpgsqlConnectionStringBuilder Parse(string spec, int connectTimeoutSeconds = 15)
    {
        Uri uri;
        try { uri = new Uri(spec.Trim()); }
        catch (UriFormatException ex) { throw new BackupException("A URL do banco de backup é inválida.", ex); }
        if (!IsPostgres(spec) || uri.Host.Length == 0) throw new BackupException("A URL do banco de backup é inválida.");
        var userInfo = uri.UserInfo;
        var colon = userInfo.IndexOf(':');
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
            Username = Uri.UnescapeDataString(colon < 0 ? userInfo : userInfo[..colon]),
            Password = colon < 0 ? null : Uri.UnescapeDataString(userInfo[(colon + 1)..]),
            Timeout = connectTimeoutSeconds,
            CommandTimeout = 300,
            Pooling = false, // uma conexão por cópia; o pooler do Supabase/Neon já faz o resto
            SslMode = SslMode.Prefer,
        };
        if (builder.Database.Length == 0) builder.Database = "postgres";
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]).ToLowerInvariant();
            var value = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..]);
            switch (key)
            {
                case "sslmode":
                    builder.SslMode = value.ToLowerInvariant() switch
                    {
                        "disable" => SslMode.Disable,
                        "allow" => SslMode.Allow,
                        "prefer" => SslMode.Prefer,
                        "require" => SslMode.Require, // como no libpq: cifra, mas não valida a cadeia do certificado (só verify-ca e verify-full validam)
                        "verify-ca" => SslMode.VerifyCA,
                        "verify-full" => SslMode.VerifyFull,
                        _ => throw new BackupException("O sslmode da URL do banco de backup não é reconhecido."),
                    };
                    break;
                case "connect_timeout" when int.TryParse(value, out var seconds) && seconds > 0: builder.Timeout = seconds; break;
                case "application_name": builder.ApplicationName = value; break;
                case "options": builder.Options = value; break;
            }
        }
        return builder;
    }
}
