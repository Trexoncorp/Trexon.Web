using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Trexon.Web.Tests;

public sealed class PortHostingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Built_application_runs_on_the_requested_port_overrides_Kestrel_endpoints_and_stops_asynchronously()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var builder = TrexonApp.Create();
        builder.Builder.Logging.ClearProviders();
        builder.Builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var tracker = new DisposalTracker();
        builder.Services.AddSingleton(_ => tracker);
        builder.Map(app => app.MapGet("/ready", (DisposalTracker _) => "ready"));
        await using var web = await builder.BuildAsync(Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = web.Lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = web.RunAsync(port, stop.Token);
        try
        {
            await Task.WhenAny(started.Task, running).WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (!started.Task.IsCompleted) await running;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal($"http://0.0.0.0:{port}", Assert.Single(web.Urls));
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            Assert.Equal("ready", await client.GetStringAsync($"http://127.0.0.1:{port}/ready", Token));
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        Assert.True(tracker.Disposed);
    }

    [Fact]
    public async Task Builder_shortcut_initializes_before_listening_supports_port_zero_and_disposes_its_host()
    {
        var builder = TrexonApp.Create();
        builder.Builder.Logging.ClearProviders();
        var module = new InitializingModule();
        var tracker = new DisposalTracker();
        builder.AddModule(module);
        builder.Services.AddSingleton(_ => tracker);
        var started = new TaskCompletionSource<WebApplication>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Map(web =>
        {
            web.MapGet("/ready", (DisposalTracker _) => module.Completed);
            web.Lifetime.ApplicationStarted.Register(() => started.TrySetResult(web));
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = builder.RunAsync(0, stop.Token);
        try
        {
            await Task.WhenAny(started.Task, running).WaitAsync(TimeSpan.FromSeconds(10), Token);
            if (!started.Task.IsCompleted) await running;
            var web = await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var address = new Uri(Assert.Single(web.Urls));
            Assert.Equal("http", address.Scheme);
            Assert.Equal("0.0.0.0", address.Host);
            Assert.InRange(address.Port, 1, 65535);
            using var client = new HttpClient();
            Assert.Equal("true", await client.GetStringAsync($"http://127.0.0.1:{address.Port}/ready", Token));
        }
        finally
        {
            await stop.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        Assert.True(tracker.Disposed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Invalid_ports_fail_before_consuming_the_builder_or_starting_the_host(int port)
    {
        var builder = TrexonApp.Create();
        builder.Builder.Logging.ClearProviders();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => builder.RunAsync(port, Token));
        // Failed validation leaves the same builder usable.
        await using var web = await builder.BuildAsync(Token);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => web.RunAsync(port, Token));
        Assert.False(web.Lifetime.ApplicationStarted.IsCancellationRequested);
    }
}
