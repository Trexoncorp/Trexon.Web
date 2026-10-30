using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web.Security;
using Trexon.Web.Sockets;
using Trexon.Web.Users;

namespace Trexon.Web.Tests;

public sealed class WebSocketConsoleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Console_is_available_with_docs_even_before_socket_registration()
    {
        await using var host = await TestHost.Start();
        var redirect = await host.Client.GetAsync("/docs/ws", Token);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/docs/ws/", redirect.Headers.Location!.ToString());
        var html = await host.Client.GetAsync("/docs/ws/", Token);
        Assert.Equal("text/html", html.Content.Headers.ContentType!.MediaType);
        Assert.Contains("WebSocket console", await html.Content.ReadAsStringAsync(Token));
        var script = await host.Client.GetAsync("/docs/ws/console.js", Token);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        var config = await host.Json("/docs/ws/config.json");
        Assert.Equal("../../openapi.json", (string?)config["schemaUrl"]);
        Assert.Equal("../../", (string?)config["appUrl"]);
        Assert.Null(config["auth"]);
        var document = await host.Json("/openapi.json");
        Assert.DoesNotContain("/docs/ws", document["paths"]!.ToJsonString());
    }

    [Fact]
    public async Task Disabled_docs_hide_the_console_and_its_assets()
    {
        await using var host = await TestHost.Start(app => app.Options.EnableDocs = false);
        foreach (var path in new[] { "/docs/ws", "/docs/ws/", "/docs/ws/console.js", "/docs/ws/config.json" })
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(path, Token)).StatusCode);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task Cookie_login_is_offered_only_with_both_account_and_cookie_modules(bool users, bool cookies, bool expected)
    {
        await using var host = await TestHost.Start(app => { if (cookies) app.UseCookies(); }, users: users);
        var config = await host.Json("/docs/ws/config.json");
        if (expected)
        {
            Assert.Equal("../../auth/cookie/login", (string?)config["auth"]!["loginUrl"]);
            Assert.Equal("../../auth/cookie/csrf", (string?)config["auth"]!["csrfUrl"]);
            Assert.Equal("../../auth/cookie/logout", (string?)config["auth"]!["logoutUrl"]);
        }
        else Assert.Null(config["auth"]);
    }

    [Fact]
    public async Task Console_uses_custom_docs_schema_account_prefixes_and_reverse_proxy_mount()
    {
        var database = Path.Combine(Path.GetTempPath(), "trexon-ws-docs-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var host = await TestHost.Start(app =>
            {
                app.Options.DocsPath = "/reference/api";
                app.Options.OpenApiPath = "/schema/custom.json";
                app.UseJwt(TestHost.Jwt).UseCookies().UseUsers(database, options => options.Prefix = "/identity");
                app.Services.AddSingleton<IStartupFilter, MountPath>();
            });
            var response = await host.Client.GetAsync("/service/reference/api/ws", Token);
            Assert.Equal("/service/reference/api/ws/", response.Headers.Location!.ToString());
            var config = await host.Json("/service/reference/api/ws/config.json");
            var root = new Uri("https://localhost/service/reference/api/ws/");
            Assert.Equal("/service/schema/custom.json", new Uri(root, (string)config["schemaUrl"]!).AbsolutePath);
            Assert.Equal("/service/", new Uri(root, (string)config["appUrl"]!).AbsolutePath);
            Assert.Equal("/service/identity/cookie/login", new Uri(root, (string)config["auth"]!["loginUrl"]!).AbsolutePath);
            Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/service/reference/api/ws/console.js", Token)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/docs/ws/", Token)).StatusCode);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(database + suffix);
        }
    }

    [Fact]
    public async Task Contract_contains_parsed_route_parameters_and_a_link_to_the_console()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<ConsoleRouteSocket>().AddSocket<DefaultRouteSocket>());
        var document = await host.Json("/openapi.json");
        Assert.Contains("[Open WebSocket console](./ws)", (string)document["info"]!["description"]!);
        var endpoints = document["x-trexon-websockets"]!.AsArray();
        var parts = endpoints[0]!["segments"]!.AsArray().SelectMany(segment => segment!.AsArray()).ToArray();
        Assert.Contains(parts, part => (string?)part!["parameter"] == "id" && !(bool)part["optional"]!);
        Assert.Contains(parts, part => (string?)part!["parameter"] == "ext" && (bool)part["optional"]!);
        Assert.Contains(parts, part => (bool?)part!["separator"] == true && (string?)part["text"] == ".");
        Assert.Contains(parts, part => (string?)part!["parameter"] == "rest" && (bool)part["catchAll"]!);
        Assert.Equal("7", (string?)endpoints[1]!["segments"]![2]![0]!["default"]);
    }

    private sealed class MountPath : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => { app.UsePathBase("/service"); next(app); };
    }
}

[Socket("/ws/console/{id:int}/{name}.{ext?}/{**rest}")]
public sealed class ConsoleRouteSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(message, token);
}

[Socket("/ws/default/{id:int=7}")]
public sealed class DefaultRouteSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(message, token);
}
