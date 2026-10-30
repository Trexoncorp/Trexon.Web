using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web.Modules;
using Trexon.Web.Security;
using Trexon.Web.Sockets;
using Trexon.Web.Users;

namespace Trexon.Web.Tests;

public sealed class PrefixRoutingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/v1")]
    [InlineData("/api/v1/")]
    public async Task Global_and_router_prefixes_compose_and_preserve_binding_links_and_request_logs(string prefix)
    {
        var logs = new List<RequestLog>();
        await using var host = await TestHost.Start(app =>
        {
            app.Options.Prefix = prefix;
            app.AddRoutes<MountedRoutes>("/catalog/").UseHealthChecks().UseRequestLogging(logs.Add);
            app.Map(web => web.MapGet("/ping", () => "pong"));
        });
        using var response = await host.Client.GetAsync("/api/v1/catalog/things/42", Token);
        Assert.Equal(42, (int)(await response.Content.ReadFromJsonAsync<JsonObject>(Token))!["id"]!);
        var entry = Assert.Single(logs);
        Assert.Equal("/api/v1/catalog/things/42", entry.Path);
        Assert.Equal(response.Headers.GetValues("X-Request-ID").Single(), entry.TraceId);
        Assert.Equal("pong", await host.Client.GetStringAsync("/api/v1/ping", Token));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/v1/health", Token)).StatusCode);
        using var invalid = await host.Client.GetAsync("/api/v1/catalog/things/0", Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("/api/v1/catalog/things/0", (string?)(await invalid.Content.ReadFromJsonAsync<JsonObject>(Token))!["instance"]);
        using var failure = await host.Client.GetAsync("/api/v1/catalog/things/failure", Token);
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode);
        Assert.Equal("/api/v1/catalog/things/failure", (string?)(await failure.Content.ReadFromJsonAsync<JsonObject>(Token))!["instance"]);
        using var created = await host.Client.PostAsync("/api/v1/catalog/things", null, Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.EndsWith("/api/v1/catalog/things/42", created.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/v1/catalog/absolute", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/v1/catalog/tilde-root", Token)).StatusCode);
        foreach (var missing in new[] { "/catalog/things/42", "/api/v10/catalog/things/42", "/absolute", "/api/v1/absolute" })
        {
            using var rejected = await host.Client.GetAsync(missing, Token);
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
            Assert.True(rejected.Headers.Contains("X-Request-ID"));
        }
    }

    [Fact]
    public async Task Swagger_paths_and_servers_include_the_effective_mount_once()
    {
        await using var host = await TestHost.Start(app =>
        {
            app.Options.Prefix = "/v1";
            app.AddRoutes<MountedRoutes>("/catalog");
            app.Services.AddSingleton<IStartupFilter, ProxyMount>();
        });
        var document = await host.Json("/proxy/v1/openapi.json");
        Assert.Equal("/proxy/v1", (string?)document["servers"]![0]!["url"]);
        Assert.NotNull(document["paths"]!["/catalog/things/{id}"]);
        Assert.NotNull(document["paths"]!["/catalog/absolute"]);
        var withoutProxy = await host.Json("/v1/openapi.json");
        Assert.Equal("/v1", (string?)withoutProxy["servers"]![0]!["url"]);
        var html = await host.Client.GetStringAsync("/proxy/v1/docs/index.html", Token);
        Assert.Contains("swagger-ui", html);
        var config = await host.Client.GetStringAsync("/proxy/v1/docs/index.js", Token);
        Assert.Contains("../openapi.json", config);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/proxy/docs/index.html", Token)).StatusCode);
    }

    [Fact]
    public async Task Router_prefix_changes_before_build_without_affecting_other_classes_or_resetting_on_plain_registration()
    {
        TrexonApp? builder = null;
        await using var host = await TestHost.Start(app =>
        {
            builder = app;
            app.AddRoutes<MountedRoutes>("/old").AddRoutes<MountedRoutes>("/new")
                .AddRoutes<MountedRoutes>().AddRoutes<CounterRoutes>();
        });
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/new/things/1", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/old/things/1", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/things/1", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/counter", Token)).StatusCode);
        Assert.Throws<InvalidOperationException>(() => builder!.AddRoutes<MountedRoutes>("/changed"));
        Assert.Throws<InvalidOperationException>(() => builder!.AddSocket<ConsoleRouteSocket>("/changed"));
    }

    [Fact]
    public async Task Global_prefix_covers_accounts_swagger_login_and_authenticated_socket_console_tickets()
    {
        await using var host = await TestHost.Start(app =>
        {
            app.Options.Prefix = "/v1/";
            app.AddRoutes<AccountRoutes>("/identity").AddSocket<ConsoleBearerSocket>("/realtime");
        }, users: true);
        using var registered = await host.Client.PostAsJsonAsync("/v1/identity/auth/register",
            new { username = "prefixed", password = "prefix-test-password-123" }, Token);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var loggedIn = await host.Client.PostAsJsonAsync("/v1/identity/auth/login",
            new { username = "prefixed", password = "prefix-test-password-123" }, Token);
        loggedIn.EnsureSuccessStatusCode();
        var token = (string)(await loggedIn.Content.ReadFromJsonAsync<JsonObject>(Token))!["accessToken"]!;
        var config = await host.Json("/v1/docs/ws/config.json");
        Assert.Equal("../../identity/auth/login", (string?)config["loginUrl"]);
        Assert.Equal("../../", (string?)config["appUrl"]);
        Assert.Contains("../identity/auth/login", await host.Client.GetStringAsync("/v1/docs/index.js", Token));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/v1/docs/trexon-login.js", Token)).StatusCode);
        var schema = await host.Json("/v1/openapi.json");
        Assert.Equal("/realtime/ws/console-bearer", (string?)schema["x-trexon-websockets"]![0]!["path"]);
        using var ticketRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/docs/ws/ticket")
        {
            Content = JsonContent.Create(new { path = "/realtime/ws/console-bearer", scheme = "Bearer" })
        };
        ticketRequest.Headers.Add("Authorization", "Bearer " + token);
        using var ticketResponse = await host.Client.SendAsync(ticketRequest, Token);
        ticketResponse.EnsureSuccessStatusCode();
        var protocol = (string)(await ticketResponse.Content.ReadFromJsonAsync<JsonObject>(Token))!["protocol"]!;
        var client = host.App.GetTestServer().CreateWebSocketClient();
        client.SubProtocols.Add(protocol);
        client.ConfigureRequest = request => request.Headers.Origin = "https://localhost";
        using var socket = await client.ConnectAsync(new Uri("wss://localhost/v1/realtime/ws/console-bearer"), Token);
        var bytes = new byte[100];
        var received = await socket.ReceiveAsync(bytes.AsMemory(), Token);
        Assert.Equal("prefixed", System.Text.Encoding.UTF8.GetString(bytes, 0, received.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Token);
    }

    [Fact]
    public async Task Socket_registration_can_change_or_clear_its_prefix_without_duplicate_endpoints()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<ConsoleRouteSocket>("/old")
            .AddSocket<ConsoleRouteSocket>("/new").AddSocket<ConsoleRouteSocket>());
        var schema = await host.Json("/openapi.json");
        Assert.Equal("/new/ws/console/{id:int}/{name}.{ext?}/{**rest}",
            (string?)Assert.Single(schema["x-trexon-websockets"]!.AsArray())!["path"]);
        await using var cleared = await TestHost.Start(app => app.AddSocket<ConsoleRouteSocket>("/old").AddSocket<ConsoleRouteSocket>("/"));
        var reset = await cleared.Json("/openapi.json");
        Assert.Equal("/ws/console/{id:int}/{name}.{ext?}/{**rest}",
            (string?)Assert.Single(reset["x-trexon-websockets"]!.AsArray())!["path"]);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("//v1")]
    [InlineData("/v1//")]
    [InlineData("/v1/../admin")]
    [InlineData("/v1/{version}")]
    [InlineData("/v1?test")]
    [InlineData("/v1#test")]
    [InlineData("/v1/%2f")]
    [InlineData(null)]
    public async Task Invalid_prefixes_fail_before_build_or_registration(string? prefix)
    {
        var builder = TrexonApp.Create();
        Assert.ThrowsAny<ArgumentException>(() => builder.AddRoutes<MountedRoutes>(prefix!));
        Assert.ThrowsAny<ArgumentException>(() => builder.AddSocket<ConsoleRouteSocket>(prefix!));
        builder.Options.Prefix = prefix!;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => builder.BuildAsync(Token));
    }

    private sealed class ProxyMount : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => { app.UsePathBase("/proxy"); next(app); };
    }
}

[Routes("/things"), Tag("Things")]
public sealed class MountedRoutes
{
    [Get("{id:int}")]
    public ValueTask<object> Get([Range(1, 100)] int id) => ValueTask.FromResult<object>(new { id });
    [Post]
    public ValueTask<IActionResult> Create() => ValueTask.FromResult<IActionResult>(new CreatedAtActionResult(nameof(Get), null, new { id = 42 }, new { id = 42 }));
    [Get("failure")]
    public ValueTask<string> Failure() => throw new HttpError(409, "Conflict");
    [Get("/absolute")]
    public ValueTask<string> Absolute() => ValueTask.FromResult("absolute");
    [Get("~/tilde-root")]
    public ValueTask<string> TildeRoot() => ValueTask.FromResult("absolute");
}
