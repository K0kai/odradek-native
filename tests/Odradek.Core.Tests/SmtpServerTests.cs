using System.Net;
using System.Net.Sockets;
using System.Text;
using Odradek.Core.Actions;
using Dapper;
using Odradek.Core.Data;

namespace Odradek.Core.Tests;

/// <summary>Servidor SMTP mínimo em localhost (sem TLS) que guarda o que recebe: prova o diálogo do cliente sem sair da máquina.</summary>
public sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _loop;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<string> Commands { get; } = [];
    public string Data { get; private set; } = "";
    public string RejectAuthWith { get; init; } = "";

    public FakeSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    private async Task Serve()
    {
        using var client = await _listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        async Task Reply(string text) => await stream.WriteAsync(Encoding.ASCII.GetBytes(text + "\r\n"));
        await Reply("220 fake ready");
        while (await reader.ReadLineAsync() is { } line)
        {
            Commands.Add(line);
            if (line.StartsWith("EHLO")) await Reply("250-fake\r\n250-AUTH PLAIN LOGIN\r\n250 8BITMIME");
            else if (line.StartsWith("AUTH PLAIN")) await Reply(RejectAuthWith.Length > 0 ? RejectAuthWith : "235 ok");
            else if (line.StartsWith("MAIL FROM") || line.StartsWith("RCPT TO")) await Reply("250 ok");
            else if (line == "DATA")
            {
                await Reply("354 go");
                var data = new StringBuilder();
                while (await reader.ReadLineAsync() is { } l && l != ".") data.AppendLine(l.StartsWith("..") ? l[1..] : l);
                Data = data.ToString();
                await Reply("250 queued");
            }
            else if (line == "QUIT") { await Reply("221 bye"); break; }
            else await Reply("500 what");
        }
    }

    public void Dispose() { _listener.Stop(); try { _loop.Wait(1000); } catch (AggregateException) { } }
}

public class SmtpServerTests
{
    [Fact]
    public async Task Sends_with_auth_envelope_and_attachment_through_the_wire()
    {
        using var server = new FakeSmtpServer();
        var config = new SmtpConfig("127.0.0.1", server.Port, "user@test", "Ana <ana@test.com>", false, "secret");
        await Smtp.SendAsync(config, ["a@x.com", "b@y.com"], "Candidatura: Dev — Ana", "Linha 1\n.linha com ponto\nfim", "%PDF-1.4 fake"u8.ToArray(), "cv.pdf");
        Assert.Contains(server.Commands, c => c == "AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes("\0user@test\0secret")));
        Assert.Contains("MAIL FROM:<ana@test.com>", server.Commands); // "Nome <endereço>" vira só o endereço no envelope
        Assert.Contains("RCPT TO:<a@x.com>", server.Commands);
        Assert.Contains("RCPT TO:<b@y.com>", server.Commands);
        Assert.Contains("filename=\"cv.pdf\"", server.Data);
        var normalized = server.Data.Replace("\r\n", "").Replace("\n", "");
        Assert.Contains(Convert.ToBase64String("%PDF-1.4 fake"u8.ToArray()), normalized);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Linha 1\r\n.linha com ponto\r\nfim")), normalized);
    }

    [Fact]
    public async Task Skips_auth_when_there_is_no_user_and_reports_server_rejection()
    {
        using (var server = new FakeSmtpServer())
        {
            await Smtp.SendAsync(new SmtpConfig("127.0.0.1", server.Port, "", "me@test.com", false, ""), ["a@x.com"], "teste", "corpo");
            Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH"));
        }
        using var rejecting = new FakeSmtpServer { RejectAuthWith = "535 5.7.8 bad credentials" };
        var ex = await Assert.ThrowsAsync<IOException>(() => Smtp.SendAsync(new SmtpConfig("127.0.0.1", rejecting.Port, "u", "me@test.com", false, "wrong"), ["a@x.com"], "teste", "corpo"));
        Assert.Contains("535", ex.Message);
        Assert.Contains("bad credentials", ex.Message);
    }

    [Fact]
    public async Task Fails_fast_when_nothing_listens()
    {
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start(); var closed = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => Smtp.SendAsync(new SmtpConfig("127.0.0.1", closed, "", "me@test.com", false, ""), ["a@x.com"], "t", "c"));
    }
}

public class EmailActionsTests
{
    private static (TempDb Db, long JobId) Seed(int port, bool letter = true, bool resume = true)
    {
        var t = new TempDb();
        var job = t.AddJob(description: "Envie o CV para rh@acme.com ou noreply@acme.com", title: "Dev");
        using (var c = t.Db.Open())
        {
            if (letter) c.Execute("INSERT INTO cover_letters(job_id,language,provider,model,body,created_at,updated_at) VALUES(@job,'pt','p','m','Prezados, segue minha candidatura.','x','x')", new { job });
            if (resume)
            {
                var pdf = LetterExport.Pdf("Experiência profissional");
                var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf));
                c.Execute("INSERT INTO resumes(id,language,stored_path,file_sha256,analysis_summary,created_at,updated_at) VALUES(1,'pt','x',@sha,'resumo','x','x')", new { sha });
                c.Execute("INSERT INTO resume_files(resume_id,content) VALUES(1,@pdf)", new { pdf });
            }
        }
        new SettingsStore(t.Db).SetMany(new Dictionary<string, string>
            { ["smtp_host"] = "127.0.0.1", ["smtp_port"] = port.ToString(), ["smtp_use_tls"] = "0", ["smtp_from"] = "eu@test.com", ["candidate_name"] = "Ana" });
        return (t, job);
    }

    [Fact]
    public async Task Sends_only_detected_addresses_and_marks_the_job_applied()
    {
        using var server = new FakeSmtpServer();
        var (t, job) = Seed(server.Port);
        using var _ = t;
        var detail = await new EmailActions(t.Db).SendApplicationAsync(job, ["RH@acme.com", "intruso@evil.com"]);
        Assert.Equal("E-mail enviado para rh@acme.com", detail);
        Assert.Contains("RCPT TO:<rh@acme.com>", server.Commands);
        Assert.DoesNotContain(server.Commands, c => c.Contains("evil.com"));
        Assert.Equal("applied", t.Scalar<string>("SELECT status FROM jobs WHERE id=@job", new { job }));
        Assert.Equal("sent", t.Scalar<string>("SELECT status FROM applications WHERE job_id=@job", new { job }));
        Assert.Contains("resume_pt_", server.Data);
    }

    [Fact]
    public async Task Refuses_without_letter_resume_valid_recipient_or_when_already_applied()
    {
        using var server = new FakeSmtpServer();
        var (t, job) = Seed(server.Port, letter: false);
        using var _ = t;
        var email = new EmailActions(t.Db);
        Assert.Equal("Escolha ao menos um e-mail de contato detectado na vaga.", (await Assert.ThrowsAsync<ActionException>(() => email.SendApplicationAsync(job, ["x@y.com"]))).Message);
        Assert.Equal("A vaga não tem carta de apresentação; gere a carta antes de enviar.", (await Assert.ThrowsAsync<ActionException>(() => email.SendApplicationAsync(job, ["rh@acme.com"]))).Message);
        new JobActions(t.Db).SetStatus(job, "applied");
        Assert.Equal("Esta vaga já está marcada como aplicada.", (await Assert.ThrowsAsync<ActionException>(() => email.SendApplicationAsync(job, ["rh@acme.com"]))).Message);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("MAIL"));
    }

    [Fact]
    public async Task Records_a_failed_attempt_and_keeps_the_job_open_when_smtp_fails()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        var (t, job) = Seed(port);
        using var _ = t;
        var ex = await Assert.ThrowsAsync<ActionException>(() => new EmailActions(t.Db).SendApplicationAsync(job, ["rh@acme.com"]));
        Assert.StartsWith("Falha ao enviar o e-mail: Falha SMTP:", ex.Message);
        Assert.Equal("worth", t.Scalar<string>("SELECT status FROM jobs WHERE id=@job", new { job }));
        Assert.Equal("failed", t.Scalar<string>("SELECT status FROM applications WHERE job_id=@job", new { job }));
    }

    [Fact]
    public async Task Smtp_test_reports_missing_address_and_success()
    {
        using var server = new FakeSmtpServer();
        var (t, _) = Seed(server.Port);
        using var __ = t;
        var email = new EmailActions(t.Db);
        var ok = await email.SendTestAsync();
        Assert.True(ok.Ok);
        Assert.Equal("E-mail de teste enviado para eu@test.com.", ok.Notice);
        new SettingsStore(t.Db).SetMany(new Dictionary<string, string> { ["smtp_from"] = "", ["candidate_email"] = "" });
        var missing = await email.SendTestAsync();
        Assert.False(missing.Ok);
        Assert.Equal("Configure o e-mail do candidato ou remetente para testar.", missing.Notice);
    }
}
