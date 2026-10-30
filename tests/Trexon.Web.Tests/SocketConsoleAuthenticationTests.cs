using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web.Security;
using Trexon.Web.Sockets;

namespace Trexon.Web.Tests;

public sealed class SocketConsoleAuthenticationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Password_login_can_test_a_bearer_only_socket_without_a_cookie_module()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<ConsoleBearerSocket>(), users: true);
        var credentials = await host.RegisterLogin();
        var config = await host.Json("/docs/ws/config.json");
        Assert.Equal("../../auth/login", (string?)config["loginUrl"]);
        Assert.Equal("ticket", (string?)config["ticketUrl"]);
        Assert.Null(config["auth"]);
        var ticket = await Issue(host, "/ws/console-bearer", "Bearer", credentials.AccessToken);
        Assert.DoesNotContain(credentials.AccessToken, ticket);
        using var socket = await Connect(host, "/ws/console-bearer", ticket);
        Assert.Equal("tester", await Read(socket));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Token);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/console-bearer", ticket));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/v1")]
    public async Task Real_Kestrel_negotiates_the_ticket_protocol_and_authenticates_the_original_endpoint(string prefix)
    {
        var builder = TrexonApp.Create();
        builder.Options.Prefix = prefix;
        builder.UseJwt(TestHost.Jwt).AddSocket<ConsoleBearerSocket>();
        builder.Builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = await builder.BuildAsync(Token);
        await app.StartAsync(Token);
        try
        {
            var origin = app.Urls.Single();
            using var http = new HttpClient { BaseAddress = new Uri(origin) };
            var token = app.Services.GetRequiredService<ITokenIssuer>().Issue("user", "real-client").Token;
            using var request = TicketRequest("/ws/console-bearer", "Bearer", token);
            request.RequestUri = new Uri(prefix + "/docs/ws/ticket", UriKind.Relative);
            var response = await http.SendAsync(request, Token);
            response.EnsureSuccessStatusCode();
            var protocol = (string)(await response.Content.ReadFromJsonAsync<JsonObject>(Token))!["protocol"]!;
            using var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(protocol);
            socket.Options.SetRequestHeader("Origin", origin);
            await socket.ConnectAsync(new Uri(origin.Replace("http://", "ws://", StringComparison.Ordinal) + prefix + "/ws/console-bearer"), Token);
            Assert.Equal(protocol, socket.SubProtocol);
            Assert.Equal("real-client", await Read(socket));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Token);
        }
        finally { await app.StopAsync(Token); }
    }

    [Fact]
    public async Task Api_key_tickets_keep_the_original_scheme_and_custom_header()
    {
        await using var host = await TestHost.Start(app => app.UseApiKeys<TestApiKeys>("X-Integration-Key").AddSocket<ConsoleKeySocket>());
        var ticket = await Issue(host, "/ws/console-key", "ApiKey", "valid-test-key", "X-Integration-Key");
        using var socket = await Connect(host, "/ws/console-key", ticket);
        Assert.Equal("integration", await Read(socket));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Token);
    }

    [Fact]
    public async Task Wrong_scheme_and_missing_roles_are_still_rejected_on_upgrade()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).UseApiKeys<TestApiKeys>()
            .AddSocket<ConsoleBearerSocket>().AddSocket<ConsoleAdminSocket>());
        var keyTicket = await Issue(host, "/ws/console-bearer", "ApiKey", "valid-test-key");
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/console-bearer", keyTicket));
        var issuer = host.App.Services.GetRequiredService<ITokenIssuer>();
        var userTicket = await Issue(host, "/ws/console-admin", "Bearer", issuer.Issue("user", roles: ["User"]).Token);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/console-admin", userTicket));
        var adminTicket = await Issue(host, "/ws/console-admin", "Bearer", issuer.Issue("admin", "admin", ["Admin"]).Token);
        using var admin = await Connect(host, "/ws/console-admin", adminTicket);
        Assert.Equal("admin", await Read(admin));
        await admin.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Token);
    }

    [Fact]
    public async Task Tickets_are_bound_to_the_path_query_and_same_origin()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).AddSocket<ConsoleBearerSocket>().AddSocket<ConsoleAdminSocket>());
        var credential = host.App.Services.GetRequiredService<ITokenIssuer>().Issue("user", "tester").Token;
        var wrongPath = await Issue(host, "/ws/console-bearer", "Bearer", credential);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/console-admin", wrongPath));
        var query = await Issue(host, "/ws/console-bearer?test=one", "Bearer", credential);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/console-bearer?test=two", query));
        var crossOrigin = await Issue(host, "/ws/console-bearer", "Bearer", credential);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/console-bearer", crossOrigin, "https://untrusted.example"));
        // A rejected foreign-origin attempt did not consume the legitimate client's ticket.
        using var valid = await Connect(host, "/ws/console-bearer", crossOrigin);
        Assert.Equal("tester", await Read(valid));
        await valid.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Token);
    }

    [Fact]
    public async Task Expiry_is_checked_and_credentials_are_revalidated_after_issuance()
    {
        var clock = new ConsoleClock();
        var state = new ConsoleKeyState();
        await using var host = await TestHost.Start(app =>
        {
            app.Services.AddSingleton<TimeProvider>(clock);
            app.Services.AddSingleton(state);
            app.UseApiKeys<RevocableConsoleKey>().AddSocket<ConsoleKeySocket>();
        });
        var expired = await Issue(host, "/ws/console-key", "ApiKey", "console-test-key");
        clock.Now = clock.Now.AddSeconds(31);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/console-key", expired));
        var revoked = await Issue(host, "/ws/console-key", "ApiKey", "console-test-key");
        state.Enabled = false;
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/console-key", revoked));
    }

    [Theory]
    [InlineData("/ws/console-bearer", "wrong-token", 401)]
    [InlineData("/auth/me", "valid", 400)]
    [InlineData("https://outside.example/ws", "valid", 400)]
    [InlineData("//outside.example/ws", "valid", 400)]
    public async Task Tickets_reject_invalid_credentials_and_non_socket_targets(string path, string value, int status)
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).AddSocket<ConsoleBearerSocket>());
        var credential = value == "valid" ? host.App.Services.GetRequiredService<ITokenIssuer>().Issue("user").Token : value;
        using var request = TicketRequest(path, "Bearer", credential);
        var response = await host.Client.SendAsync(request, Token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Cross_origin_ticket_requests_and_disabled_docs_do_not_issue_tickets()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).AddSocket<ConsoleBearerSocket>());
        using var request = TicketRequest("/ws/console-bearer", "Bearer", host.App.Services.GetRequiredService<ITokenIssuer>().Issue("user").Token);
        request.Headers.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(request, Token)).StatusCode);
        await using var disabled = await TestHost.Start(app => { app.Options.EnableDocs = false; app.AddSocket<ConsoleBearerSocket>().UseJwt(TestHost.Jwt); });
        using var unavailable = TicketRequest("/ws/console-bearer", "Bearer", "test");
        Assert.Equal(HttpStatusCode.NotFound, (await disabled.Client.SendAsync(unavailable, Token)).StatusCode);
    }

    private static HttpRequestMessage TicketRequest(string path, string scheme, string credential, string header = "X-API-Key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/docs/ws/ticket") { Content = JsonContent.Create(new { path, scheme }) };
        request.Headers.Add(scheme == "Bearer" ? "Authorization" : header, scheme == "Bearer" ? "Bearer " + credential : credential);
        return request;
    }
    private static async Task<string> Issue(TestHost host, string path, string scheme, string credential, string header = "X-API-Key")
    {
        using var request = TicketRequest(path, scheme, credential, header);
        var response = await host.Client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();
        return (string)(await response.Content.ReadFromJsonAsync<JsonObject>(Token))!["protocol"]!;
    }
    private static async Task<WebSocket> Connect(TestHost host, string path, string protocol, string origin = "https://localhost")
    {
        var client = host.App.GetTestServer().CreateWebSocketClient();
        client.SubProtocols.Add(protocol);
        client.ConfigureRequest = request => request.Headers.Origin = origin;
        return await client.ConnectAsync(new Uri("wss://localhost" + path), Token);
    }
    private static async Task<string> Read(WebSocket socket)
    {
        var bytes = new byte[1024];
        var message = await socket.ReceiveAsync(bytes.AsMemory(), Token);
        return Encoding.UTF8.GetString(bytes, 0, message.Count);
    }
}

public class ConsoleIdentitySocket : SocketHandler<string>
{
    public override ValueTask OnConnectedAsync(SocketConnection<string> connection, CancellationToken token) =>
        connection.SendAsync(connection.User.Identity!.Name!, token);
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(message, token);
}
[Socket("/ws/console-bearer"), Authorize(AuthenticationSchemes = AuthSchemes.Bearer)]
public sealed class ConsoleBearerSocket : ConsoleIdentitySocket;
[Socket("/ws/console-key"), Authorize(AuthenticationSchemes = AuthSchemes.ApiKey)]
public sealed class ConsoleKeySocket : ConsoleIdentitySocket;
[Socket("/ws/console-admin"), Authorize(Roles = "Admin", AuthenticationSchemes = AuthSchemes.Bearer)]
public sealed class ConsoleAdminSocket : ConsoleIdentitySocket;
public sealed class ConsoleKeyState { public bool Enabled { get; set; } = true; }
public sealed class RevocableConsoleKey(ConsoleKeyState state) : IApiKeyValidator
{
    public ValueTask<ClaimsPrincipal?> ValidateAsync(string key, CancellationToken token) => ValueTask.FromResult(
        state.Enabled && key == "console-test-key" ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], AuthSchemes.ApiKey)) : null);
}
internal sealed class ConsoleClock : TimeProvider
{
    internal DateTimeOffset Now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
