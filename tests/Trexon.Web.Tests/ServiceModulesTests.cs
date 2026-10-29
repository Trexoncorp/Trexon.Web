using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Trexon.Web.Modules;

namespace Trexon.Web.Tests;

public sealed class ServiceModulesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Cors_allows_only_configured_origins()
    {
        await using var host = await TestHost.Start(app => app.UseCors("https://frontend.example").AddRoutes<CounterRoutes>());
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/counter");
        allowed.Headers.Add("Origin", "https://frontend.example"); allowed.Headers.Add("Access-Control-Request-Method", "GET");
        var response = await host.Client.SendAsync(allowed, Token);
        Assert.Equal("https://frontend.example", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        using var denied = new HttpRequestMessage(HttpMethod.Options, "/counter");
        denied.Headers.Add("Origin", "https://untrusted.example"); denied.Headers.Add("Access-Control-Request-Method", "GET");
        Assert.False((await host.Client.SendAsync(denied, Token)).Headers.Contains("Access-Control-Allow-Origin"));
    }
    [Fact]
    public async Task Rate_limit_returns_a_structured_429_and_retry_after()
    {
        await using var host = await TestHost.Start(app => app.UseRateLimit(1).AddRoutes<CounterRoutes>());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/counter", Token)).StatusCode);
        var rejected = await host.Client.GetAsync("/counter", Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.NotNull((await rejected.Content.ReadFromJsonAsync<JsonObject>(Token))!["traceId"]);
    }
    [Fact]
    public async Task Output_cache_reuses_only_public_successful_responses()
    {
        await using var host = await TestHost.Start(app => app.UseOutputCache().AddRoutes<CounterRoutes>());
        var first = await host.Client.GetStringAsync("/counter", Token);
        var second = await host.Client.GetStringAsync("/counter", Token);
        Assert.Equal(first, second);
        var differentQuery = await host.Client.GetStringAsync("/counter?v=other", Token);
        Assert.NotEqual(first, differentQuery);
    }
    [Fact]
    public async Task Health_checks_report_dependency_failures_without_internal_details()
    {
        await using var host = await TestHost.Start(app =>
        {
            app.UseHealthChecks();
            app.Services.AddHealthChecks().AddCheck("dependency", () => HealthCheckResult.Unhealthy("secret connection information"));
        });
        var response = await host.Client.GetAsync("/health", Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains("Unhealthy", text); Assert.DoesNotContain("secret", text);
    }
    [Fact]
    public async Task Request_logging_captures_status_and_excludes_credentials_and_query()
    {
        var entries = new List<RequestLog>();
        await using var host = await TestHost.Start(app => app.UseRequestLogging(entries.Add));
        await host.Client.GetAsync("/missing?token=secret", Token);
        var entry = Assert.Single(entries);
        Assert.Equal(404, entry.StatusCode); Assert.Equal("/missing", entry.Path);
        Assert.True(entry.ElapsedMilliseconds >= 0); Assert.NotEmpty(entry.TraceId);
    }
    [Fact]
    public async Task Async_request_log_sink_is_awaited_before_completion()
    {
        var captured = false;
        await using var host = await TestHost.Start(app => app.UseRequestLogging(async (_, token) =>
        {
            await Task.Delay(10, token);
            captured = true;
        }));
        await host.Client.GetAsync("/missing", Token);
        Assert.True(captured);
    }
    [Fact]
    public async Task A_failing_log_sink_does_not_fail_the_http_request()
    {
        await using var host = await TestHost.Start(app => app.UseRequestLogging(_ => throw new Exception("sink failed")));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/missing", Token)).StatusCode);
    }
}
[Routes("/counter")]
public sealed class CounterRoutes
{
    [Get, OutputCache(Duration = 60)] public object Get() => new { value = Guid.NewGuid() };
}
