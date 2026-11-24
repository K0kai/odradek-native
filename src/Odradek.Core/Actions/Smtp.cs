using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Odradek.Core.Actions;

public sealed record SmtpConfig(string Host, int Port, string User, string From, bool UseTls, string Password);

/// <summary>
/// Cliente SMTP mínimo (<c>jobs/apply_channels.py:send_smtp_email</c>): TLS implícito na porta 465, STARTTLS nas demais quando
/// "Usar TLS" está ligado, AUTH PLAIN/LOGIN, um anexo PDF opcional e tempo limite de 45 s. O <c>SmtpClient</c> da biblioteca padrão
/// não faz TLS implícito e os pacotes de e-mail não foram aprovados, por isso o protocolo é falado direto.
/// </summary>
public static class Smtp
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static SmtpConfig ConfigFrom(IReadOnlyDictionary<string, string> cfg, string password)
    {
        string Get(string key) => cfg.TryGetValue(key, out var v) ? v : "";
        var host = Get("smtp_host").Trim();
        if (host.Length == 0) throw new ActionException("Configure o host SMTP no painel.");
        var portText = Get("smtp_port") is { Length: > 0 } p ? p : "587";
        if (!int.TryParse(portText.Trim(), out var port)) throw new ActionException("Porta SMTP inválida.");
        var user = Get("smtp_user").Trim();
        var from = new[] { Get("smtp_from"), Get("candidate_email"), user }.Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0) ?? "";
        if (from.Length == 0) throw new ActionException("Configure o e-mail remetente (SMTP from / candidate_email).");
        if (password.Length == 0 && user.Length > 0) throw new ActionException("Configure a senha SMTP no painel.");
        return new SmtpConfig(host, port, user, from, (Get("smtp_use_tls") is { Length: > 0 } t ? t : "1") == "1", password);
    }

    private static string EncodeHeader(string value) =>
        value.All(c => c < 128) ? value : "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

    private static string Wrap64(byte[] data) =>
        string.Join("\r\n", Convert.ToBase64String(data).Chunk(76).Select(c => new string(c)));

    /// <summary>Mensagem MIME completa (cabeçalhos + corpo base64 UTF-8 + anexo PDF opcional).</summary>
    public static string BuildMessage(SmtpConfig config, IReadOnlyList<string> to, string subject, string body, byte[]? attachment, string? attachmentName)
    {
        var sb = new StringBuilder();
        sb.Append("From: ").Append(config.From).Append("\r\n");
        sb.Append("To: ").Append(string.Join(", ", to)).Append("\r\n");
        sb.Append("Subject: ").Append(EncodeHeader(subject)).Append("\r\n");
        sb.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(config.Host).Append(">\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        var text = Wrap64(Encoding.UTF8.GetBytes(body.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        if (attachment is null)
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(text).Append("\r\n");
            return sb.ToString();
        }
        var boundary = "odradek-" + Guid.NewGuid().ToString("N");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n");
        sb.Append($"--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{text}\r\n");
        var name = attachmentName ?? "curriculo.pdf";
        sb.Append($"--{boundary}\r\nContent-Type: application/pdf; name=\"{name}\"\r\nContent-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"{name}\"\r\n\r\n");
        sb.Append(Wrap64(attachment)).Append($"\r\n--{boundary}--\r\n");
        return sb.ToString();
    }

    private static string AddressOnly(string address) => new MailAddress(address.Trim()).Address;

    public static async Task SendAsync(SmtpConfig config, IReadOnlyList<string> to, string subject, string body, byte[]? attachment = null, string? attachmentName = null)
    {
        var message = BuildMessage(config, to, subject, body, attachment, attachmentName);
        using var cts = new CancellationTokenSource(Timeout);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(config.Host, config.Port, cts.Token);
        Stream stream = tcp.GetStream();
        if (config.Port == 465) stream = await Secure(stream, config.Host, cts.Token);

        async Task<(int Code, string Text)> Read()
        {
            var text = new StringBuilder();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new IOException("O servidor SMTP fechou a conexão.");
                text.AppendLine(line);
                if (line.Length >= 4 && line[3] == ' ' && int.TryParse(line.AsSpan(0, 3), out var code)) return (code, text.ToString());
                if (line.Length < 4) return (0, text.ToString());
            }
        }
        async Task<string> Command(string? line, params int[] expected)
        {
            if (line is not null) await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), cts.Token);
            var (code, text) = await Read();
            if (!expected.Contains(code)) throw new IOException($"SMTP {code}: {text.Trim()}");
            return text;
        }

        await Command(null, 220);
        var hello = await Command("EHLO odradek.local", 250);
        if (config.Port != 465 && config.UseTls)
        {
            await Command("STARTTLS", 220);
            stream = await Secure(stream, config.Host, cts.Token);
            hello = await Command("EHLO odradek.local", 250);
        }
        if (config.User.Length > 0)
        {
            if (hello.Contains("PLAIN", StringComparison.OrdinalIgnoreCase))
                await Command("AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{config.User}\0{config.Password}")), 235);
            else
            {
                await Command("AUTH LOGIN", 334);
                await Command(Convert.ToBase64String(Encoding.UTF8.GetBytes(config.User)), 334);
                await Command(Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Password)), 235);
            }
        }
        await Command($"MAIL FROM:<{AddressOnly(config.From)}>", 250);
        foreach (var recipient in to) await Command($"RCPT TO:<{AddressOnly(recipient)}>", 250, 251);
        await Command("DATA", 354);
        var stuffed = string.Join("\r\n", message.Split("\r\n").Select(l => l.StartsWith('.') ? "." + l : l));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(stuffed.TrimEnd('\r', '\n') + "\r\n.\r\n"), cts.Token);
        var (done, doneText) = await Read();
        if (done != 250) throw new IOException($"SMTP {done}: {doneText.Trim()}");
        try { await Command("QUIT", 221); } catch (Exception) { /* a mensagem já foi aceita */ }
    }

    private static async Task<Stream> Secure(Stream inner, string host, CancellationToken token)
    {
        var ssl = new SslStream(inner, false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, token);
        return ssl;
    }
}
