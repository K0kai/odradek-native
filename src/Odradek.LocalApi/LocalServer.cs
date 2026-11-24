using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Odradek.LocalApi;

/// <summary>Servidor HTTP local em 127.0.0.1:8765: a porta fixa que a extensão do navegador e o Qpid esperam.</summary>
public sealed class LocalServer : IAsyncDisposable
{
    public const string Host = "127.0.0.1";
    public const string ServiceName = "odradek-scraper";

    public static int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) && p is > 0 and < 65536 ? p : 8765;
    public static string Url => $"http://{Host}:{Port}/";

    private WebApplication? _app;
    private readonly ApiServices? _services;
    private readonly int _port;
    private long _lastRequestTicks;

    /// <summary>Quando a extensão ou o Qpid fizeram a última requisição (UTC; <see cref="DateTime.MinValue"/> se nunca). A atualização automática só reinicia com o servidor quieto.</summary>
    public DateTime LastRequestUtc => new(Interlocked.Read(ref _lastRequestTicks), DateTimeKind.Utc);

    /// <param name="services">As rotas da extensão e do Qpid; sem isso só o <c>/health</c> responde (como na etapa 0).</param>
    /// <param name="port">Porta de escuta; o padrão é a fixa (8765, ou <c>PORT</c>). Os testes usam outra.</param>
    public LocalServer(ApiServices? services = null, int? port = null)
    {
        _services = services;
        _port = port ?? Port;
    }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, _port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120); // as telas consultam a cada 15-60 s: conexões ociosas não podem fechar antes
        });
        builder.Host.ConfigureHostOptions(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != "/health") Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks); // o /health é só sonda (inclusive a do atualizador)
            await next();
        });
        MapRoutes(app, _services);
        await app.StartAsync(cancellation);
        _app = app;
    }

    private static void MapRoutes(WebApplication app, ApiServices? services)
    {
        if (services is not null) { ApiRoutes.Map(app, services); return; }
        app.MapGet("/health", () => Results.Text("{\"ok\":true,\"service\":\"" + ServiceName + "\"}", "application/json"));
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
        _app = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public enum PortState { Free, Ours, Other }

    /// <summary>'Ours' = já há um Odradek nessa porta; 'Other' = outra coisa responde; 'Free' = ninguém (igual ao <c>probe()</c> do Python).</summary>
    public static async Task<PortState> ProbeAsync(TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(1.5);
        try
        {
            using var http = new HttpClient { Timeout = limit };
            using var response = await http.GetAsync(Url + "health");
            if (!response.IsSuccessStatusCode) return PortState.Other;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.TryGetProperty("service", out var service) && service.GetString() == ServiceName
                ? PortState.Ours : PortState.Other;
        }
        catch (Exception)
        {
            // sem resposta HTTP válida: ou está livre ou outra coisa ocupa a porta
        }
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(limit);
            await client.ConnectAsync(IPAddress.Loopback, Port, cts.Token);
            return PortState.Other;
        }
        catch (Exception)
        {
            return PortState.Free;
        }
    }
}
