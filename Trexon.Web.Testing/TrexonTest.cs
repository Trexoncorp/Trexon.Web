using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Trexon.Web.Testing;

/// <summary>An in-process application with HTTP and WebSocket clients. No TCP port is opened.</summary>
public sealed class TrexonTest : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _disposed;
    private TrexonTest(WebApplication app, HttpClient client) { _app = app; Client = client; }
    /// <summary>The test HTTP client. BaseAddress defaults to http://localhost.</summary>
    public HttpClient Client { get; }
    /// <summary>The built application's root provider. Create an async scope for scoped services.</summary>
    public IServiceProvider Services => _app.Services;
    /// <summary>Builds and starts an isolated TestServer. Overrides run after application registration and before startup.</summary>
    public static async Task<TrexonTest> CreateAsync(Action<TrexonApp> configure,
        Action<IServiceCollection>? overrides = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = TrexonApp.Create(new WebApplicationOptions { EnvironmentName = "Testing" });
        configure(builder);
        overrides?.Invoke(builder.Services);
        builder.Builder.WebHost.UseTestServer();
        WebApplication? app = null;
        try
        {
            app = await builder.BuildAsync(cancellationToken);
            await app.StartAsync(cancellationToken);
            var client = app.GetTestClient();
            client.BaseAddress = new Uri("http://localhost");
            return new(app, client);
        }
        catch { if (app != null) await app.DisposeAsync(); throw; }
    }
    /// <summary>Creates a WebSocket client for the same in-process server, including the real authentication pipeline.</summary>
    public WebSocketClient CreateWebSocketClient() => _app.GetTestServer().CreateWebSocketClient();
    /// <summary>Stops the host and asynchronously disposes owned services, even when stopping fails.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Client.Dispose();
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await _app.StopAsync(timeout.Token); }
        finally { await _app.DisposeAsync(); }
    }
}
