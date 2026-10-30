using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OutputCaching;
using Trexon.Logging;
using Trexon.Web.Modules;
using Trexon.Web.Security;

namespace Trexon.Web.Tests;

// Log is process-wide. Keep reconfiguration isolated from the other host tests.
[CollectionDefinition("Process-wide request logger", DisableParallelization = true)]
public sealed class RequestLoggingCollection;

[Collection("Process-wide request logger")]
public sealed class RequestLoggingTests : IDisposable
{
    private readonly CaptureSink _sink = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public RequestLoggingTests() => Log.Configure(config => config
        .Enrich.WithProperty("Application", "request-log-tests").WriteTo.Sink(_sink));

    public void Dispose() => Log.Configure(config => config.WriteTo.Console());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requests_use_configured_Log_Info_by_default_including_cache_docs_and_errors(bool useModule)
    {
        await using (var host = await TestHost.Start(app =>
        {
            app.UseJwt(TestHost.Jwt).UseOutputCache().AddRoutes<LoggedRoutes>();
            if (useModule) app.UseRequestLogging();
        }))
        {
            string? cached = null;
            foreach (var (path, status) in new[]
            {
                ("/logged/ok", 200), ("/logged/ok", 200), ("/missing", 404),
                ("/logged/failure", 500), ("/logged/private", 401), ("/docs/index.html", 200)
            })
            {
                using var response = await host.Client.GetAsync(path, Token);
                Assert.Equal(status, (int)response.StatusCode);
                var traceId = response.Headers.GetValues("X-Request-ID").Single();
                var entry = Assert.Single(_sink.Events, e => Equals(e.Properties["TraceId"], traceId));
                Assert.Equal(LogLevel.Information, entry.Level);
                Assert.Equal("GET", entry.Properties["Method"]);
                Assert.Equal(path, entry.Properties["Path"]);
                Assert.Equal(status, entry.Properties["StatusCode"]);
                Assert.True(Assert.IsType<double>(entry.Properties["ElapsedMs"]) >= 0);
                Assert.Equal("request-log-tests", entry.Properties["Application"]);
                if (path == "/logged/ok")
                {
                    var body = await response.Content.ReadAsStringAsync(Token);
                    if (cached != null) Assert.Equal(cached, body);
                    cached = body;
                }
            }
        }
        Assert.Equal(6, _sink.Events.Count);
        // Trexon.Web must neither reconfigure nor shut down the application's Log.
        Log.Info("Application still owns the logger");
        Assert.Equal("Application still owns the logger", _sink.Events.Last().Message);
    }

    [Fact]
    public async Task Default_event_excludes_query_body_cookies_and_authentication_headers()
    {
        await using var host = await TestHost.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/missing?token=query-secret");
        request.Headers.Add("Authorization", "Bearer bearer-secret");
        request.Headers.Add("X-API-Key", "api-secret");
        request.Headers.Add("Cookie", "session=cookie-secret");
        request.Content = new StringContent("body-secret");
        using var response = await host.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var entry = Assert.Single(_sink.Events);
        Assert.Equal("POST", entry.Properties["Method"]);
        Assert.Equal("/missing", entry.Properties["Path"]);
        Assert.DoesNotContain("secret", entry.Message);
        Assert.Equal(new[] { "Application", "ElapsedMs", "Method", "Path", "StatusCode", "TraceId" },
            entry.Properties.Keys.Order().ToArray());
    }

    [Fact]
    public async Task Rate_limiter_rejections_are_logged_once_with_final_status()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).UseRateLimit(1).AddRoutes<LoggedRoutes>());
        using var first = await host.Client.GetAsync("/logged/ok", Token);
        using var second = await host.Client.GetAsync("/logged/ok", Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(new[] { 200, 429 }, _sink.Events.Select(e => (int)e.Properties["StatusCode"]!).ToArray());
    }

    [Fact]
    public async Task Custom_sink_replaces_default_without_duplicate_Log_Info_events()
    {
        var entries = new List<RequestLog>();
        await using var host = await TestHost.Start(app => app.UseRequestLogging(entries.Add));
        using var response = await host.Client.GetAsync("/missing", Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(entries);
        Assert.Empty(_sink.Events);
    }

    private sealed class CaptureSink : ILogSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
        public void Flush() { }
        public void Dispose() { }
    }
}

[Routes("/logged")]
public sealed class LoggedRoutes
{
    [Get("ok"), OutputCache(Duration = 60)]
    public ValueTask<string> Ok() => ValueTask.FromResult(Guid.NewGuid().ToString());
    [Get("failure")]
    public ValueTask<string> Failure() => throw new InvalidOperationException("internal error");
    [Get("private"), Authorize]
    public ValueTask<string> Private() => ValueTask.FromResult("private");
}
