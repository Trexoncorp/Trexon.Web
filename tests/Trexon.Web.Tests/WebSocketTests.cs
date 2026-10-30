using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web.Security;
using Trexon.Web.Sockets;

namespace Trexon.Web.Tests;

public sealed class WebSocketTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task One_registration_handles_plain_text_unicode_empty_messages_and_fragmentation()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<EchoSocket>());
        using var socket = await Connect(host, "/ws/echo");
        var value = string.Concat(Enumerable.Repeat("Привет 👋 ", 800));
        var bytes = Encoding.UTF8.GetBytes(value);
        await socket.SendAsync(bytes.AsMemory(0, 13), WebSocketMessageType.Text, false, Token);
        await socket.SendAsync(bytes.AsMemory(13), WebSocketMessageType.Text, true, Token);
        Assert.Equal(value, (await Read(socket)).Text);
        await Send(socket, ""); Assert.Equal("", (await Read(socket)).Text);
        await Close(socket);
    }
    [Fact]
    public async Task Typed_messages_validate_nested_models_and_custom_async_rules_before_dispatch()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<TypedSocket>().AddValidator<SocketInput, SocketValidator>());
        using var socket = await Connect(host, "/ws/typed");
        await Send(socket, "{broken"); Assert.Contains("invalid_json", (await Read(socket)).Text);
        await Send(socket, "null"); Assert.Contains("validation_error", (await Read(socket)).Text);
        await Send(socket, "{\"name\":\"\",\"address\":{\"city\":\"\"}}");
        var errors = JsonNode.Parse((await Read(socket)).Text)!;
        Assert.NotNull(errors["error"]!["fields"]!["Name"]);
        Assert.NotNull(errors["error"]!["fields"]!["Address.City"]);
        await Send(socket, "{\"name\":\"reserved\",\"address\":{\"city\":\"Moscow\"}}");
        Assert.Contains("reserved", (await Read(socket)).Text);
        await Send(socket, "{\"name\":\"Alice\",\"address\":{\"city\":\"Moscow\"}}");
        Assert.Equal(5, (int)JsonNode.Parse((await Read(socket)).Text)!["id"]!);
        await Close(socket);
    }
    [Fact]
    public async Task Groups_isolate_rooms_and_handlers_with_the_same_message_type()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<RoomSocket>().AddSocket<EchoSocket>());
        using var alice = await Connect(host, "/ws/rooms/one");
        using var bob = await Connect(host, "/ws/rooms/one");
        using var other = await Connect(host, "/ws/rooms/two");
        using var echo = await Connect(host, "/ws/echo");
        await Send(alice, "hello");
        Assert.Equal("hello", (await Read(alice)).Text); Assert.Equal("hello", (await Read(bob)).Text);
        // Each unrelated connection must receive its own first message, never the broadcast from room one.
        await Send(other, "other room"); Assert.Equal("other room", (await Read(other)).Text);
        await Send(echo, "echo only"); Assert.Equal("echo only", (await Read(echo)).Text);
        await Close(alice); await Close(bob); await Close(other); await Close(echo);
    }
    [Fact]
    public async Task Scoped_handlers_are_distinct_and_lifecycle_callbacks_run_once_before_disposal()
    {
        var state = new SocketLifecycle();
        await using var host = await TestHost.Start(app => { app.Services.AddSingleton(state); app.AddSocket<LifecycleSocket>(); });
        using var first = await Connect(host, "/ws/lifecycle");
        using var second = await Connect(host, "/ws/lifecycle");
        var firstId = (await Read(first)).Text; var secondId = (await Read(second)).Text;
        Assert.NotEqual(firstId, secondId);
        await Close(first); await Close(second);
        await state.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, state.Connected); Assert.Equal(2, state.Disconnected); Assert.Equal(2, state.DisposeCount);
    }
    [Fact]
    public async Task Origin_checks_default_to_same_origin_and_can_allow_explicit_clients()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<EchoSocket>().UseWebSockets(options => options.AllowedOrigins = ["https://trusted.example"]));
        using var same = await Connect(host, "/ws/echo", request => request.Headers.Origin = "https://localhost"); await Close(same);
        using var trusted = await Connect(host, "/ws/echo", request => request.Headers.Origin = "https://trusted.example"); await Close(trusted);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/echo", request => request.Headers.Origin = "https://untrusted.example"));
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/echo", request => request.Headers.Origin = "null"));
    }
    [Fact]
    public async Task Missing_origin_can_be_rejected_and_regular_http_gets_a_problem_response()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<EchoSocket>().UseWebSockets(options => options.AllowMissingOrigin = false));
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/echo"));
        var response = await host.Client.GetAsync("/ws/echo", Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
    [Fact]
    public async Task Authorization_checks_jwt_roles_before_accepting_the_connection()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).AddSocket<AdminSocket>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/ws/admin", Token)).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/admin"));
        var issuer = host.App.Services.GetRequiredService<ITokenIssuer>();
        var user = issuer.Issue("user", roles: ["User"]).Token;
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/admin", request => request.Headers.Authorization = "Bearer " + user));
        var admin = issuer.Issue("admin", roles: ["Admin"]).Token;
        using var socket = await Connect(host, "/ws/admin", request => request.Headers.Authorization = "Bearer " + admin);
        await Send(socket, "authorized"); Assert.Equal("authorized", (await Read(socket)).Text);
        await Close(socket);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_messages_are_rejected_even_when_fragmented(bool fragmented)
    {
        await using var host = await TestHost.Start(app => app.AddSocket<EchoSocket>().UseWebSockets(options => options.MaxMessageBytes = 16));
        using var socket = await Connect(host, "/ws/echo");
        var bytes = Encoding.UTF8.GetBytes(new string('x', 17));
        if (fragmented) await socket.SendAsync(bytes.AsMemory(0, 8), WebSocketMessageType.Text, false, Token);
        await socket.SendAsync(bytes.AsMemory(fragmented ? 8 : 0), WebSocketMessageType.Text, true, Token);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, (await Read(socket)).Status);
        await Close(socket);
    }
    [Fact]
    public async Task Binary_input_is_rejected_and_unexpected_errors_do_not_leak_details()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<EchoSocket>().AddSocket<FailingSocket>());
        using var binary = await Connect(host, "/ws/echo");
        await binary.SendAsync(new byte[] { 1, 2 }.AsMemory(), WebSocketMessageType.Binary, true, Token);
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, (await Read(binary)).Status); await Close(binary);
        using var failing = await Connect(host, "/ws/failing"); await Send(failing, "fail");
        var close = await Read(failing);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, close.Status);
        Assert.DoesNotContain("secret", close.Text); await Close(failing);
    }
    [Fact]
    public async Task Message_rate_limits_apply_after_the_handshake()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<EchoSocket>().UseWebSockets(options => options.MessagesPerMinute = 1));
        using var socket = await Connect(host, "/ws/echo");
        await Send(socket, "one"); Assert.Equal("one", (await Read(socket)).Text);
        await Send(socket, "two"); Assert.Equal(WebSocketCloseStatus.PolicyViolation, (await Read(socket)).Status);
        await Close(socket);
    }
    [Fact]
    public async Task Intentional_application_errors_keep_the_connection_usable()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<BusinessSocket>());
        using var socket = await Connect(host, "/ws/business");
        await Send(socket, "conflict"); Assert.Contains("application_error", (await Read(socket)).Text);
        await Send(socket, "ok"); Assert.Equal("ok", (await Read(socket)).Text);
        await Close(socket);
    }
    [Fact]
    public async Task An_oversized_server_broadcast_does_not_disconnect_other_clients()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<OversizedSocket>().UseWebSockets(options => options.MaxMessageBytes = 256));
        using var failing = await Connect(host, "/ws/oversized");
        using var healthy = await Connect(host, "/ws/oversized");
        await Send(failing, "oversized"); Assert.Equal(WebSocketCloseStatus.InternalServerError, (await Read(failing)).Status); await Close(failing);
        await Send(healthy, "healthy"); Assert.Equal("healthy", (await Read(healthy)).Text); await Close(healthy);
    }
    [Fact]
    public async Task Optional_route_parameters_work_without_extra_binding_setup()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<OptionalSocket>());
        using var socket = await Connect(host, "/ws/optional");
        await Send(socket, "hello"); Assert.Equal("lobby:hello", (await Read(socket)).Text); await Close(socket);
    }
    [Fact]
    public async Task Server_can_close_from_its_connect_callback_without_a_manual_receive_loop()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<ClosingSocket>());
        using var socket = await Connect(host, "/ws/closing");
        var close = await Read(socket); Assert.Equal(WebSocketCloseStatus.NormalClosure, close.Status);
        Assert.Equal("bye", close.Text); await Close(socket);
    }
    [Fact]
    public async Task Application_shutdown_cancels_idle_sockets()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<EchoSocket>());
        using var socket = await Connect(host, "/ws/echo");
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.App.StopAsync(stopping.Token);
        Assert.True(host.App.Lifetime.ApplicationStopping.IsCancellationRequested);
    }
    [Fact]
    public async Task Native_ClientWebSocket_works_over_real_Kestrel_HTTP()
    {
        var app = TrexonApp.Create(); app.AddSocket<EchoSocket>();
        app.Builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var web = await app.BuildAsync(Token);
        await web.StartAsync(Token);
        using var socket = new ClientWebSocket();
        var address = web.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/echo";
        try
        {
            await socket.ConnectAsync(new Uri(address), Token);
            await Send(socket, "real network"); Assert.Equal("real network", (await Read(socket)).Text);
            await Close(socket);
        }
        finally { await web.StopAsync(Token); }
    }
    [Fact]
    public async Task Concurrent_broadcasts_are_serialized_per_recipient_without_losing_messages()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<ReadyRoomSocket>());
        using var first = await Connect(host, "/ws/ready-rooms/shared");
        using var second = await Connect(host, "/ws/ready-rooms/shared");
        // A completed HTTP upgrade does not guarantee OnConnectedAsync has joined the room.
        Assert.Equal("ready", (await Read(first)).Text);
        Assert.Equal("ready", (await Read(second)).Text);
        var incoming = Enumerable.Range(0, 12).Select(i => "message-" + i).ToArray();
        await Task.WhenAll(SendAll(first, incoming[..6]), SendAll(second, incoming[6..]));
        foreach (var socket in new[] { first, second })
        {
            var received = new List<string>();
            for (var i = 0; i < incoming.Length; i++) received.Add((await Read(socket)).Text);
            Assert.Equal(incoming.Order(), received.Order());
            await Close(socket);
        }
        static async Task SendAll(WebSocket socket, string[] messages)
        { foreach (var message in messages) await Send(socket, message); }
    }
    [Fact]
    public async Task Invalid_UTF8_closes_with_a_protocol_error()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<EchoSocket>());
        using var socket = await Connect(host, "/ws/echo");
        await socket.SendAsync(new byte[] { 0xC3, 0x28 }.AsMemory(), WebSocketMessageType.Text, true, Token);
        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, (await Read(socket)).Status); await Close(socket);
    }
    [Fact]
    public async Task Cookie_authentication_works_for_upgrade_and_rejects_foreign_origins()
    {
        await using var host = await TestHost.Start(app => app.UseCookies().AddSocket<UserSocket>(), users: true);
        await host.RegisterLogin();
        var csrfResponse = await host.Client.GetAsync("/auth/cookie/csrf", Token);
        var csrf = (await csrfResponse.Content.ReadFromJsonAsync<JsonObject>(Token))!["requestToken"]!.ToString();
        host.Client.DefaultRequestHeaders.Add("Cookie", csrfResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        host.Client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        var login = await host.Client.PostAsJsonAsync("/auth/cookie/login", new { username = "tester", password = "long-test-password-123" }, Token);
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var socket = await Connect(host, "/ws/user", request => { request.Headers.Cookie = cookie; request.Headers.Origin = "https://localhost"; });
        await Send(socket, "hello"); Assert.Equal("tester", (await Read(socket)).Text); await Close(socket);
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(host, "/ws/user", request => { request.Headers.Cookie = cookie; request.Headers.Origin = "https://foreign.example"; }));
    }
    [Fact]
    public async Task Swagger_lists_socket_contracts_without_pretending_they_are_http_operations()
    {
        await using var host = await TestHost.Start(app => app.AddSocket<TypedSocket>());
        var document = await host.Json("/openapi.json");
        Assert.Contains("/ws/typed", document["info"]!["description"]!.ToString());
        var descriptor = document["x-trexon-websockets"]![0]!;
        Assert.Equal("json", descriptor["inputFormat"]!.ToString());
        Assert.NotNull(descriptor["input"]!["$ref"]); Assert.NotNull(descriptor["output"]!["$ref"]);
        Assert.NotNull(document["components"]!["schemas"]![typeof(SocketInput).FullName!]);
        Assert.Null(document["paths"]!["/ws/typed"]);
    }
    [Fact]
    public async Task Duplicate_socket_paths_conflict_with_http_routes_and_unsupported_configuration_fails_early()
    {
        var builder = TrexonApp.Create().AddSocket<EchoSocket>().AddSocket<ConflictingSocket>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(Token));
        var invalid = TrexonApp.Create().AddSocket<EchoSocket>().UseWebSockets(options => options.AllowedOrigins = ["*"]);
        await Assert.ThrowsAsync<ArgumentException>(() => invalid.BuildAsync(Token));
        var protectedApp = TrexonApp.Create().AddSocket<AdminSocket>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => protectedApp.BuildAsync(Token));
    }
    private static Task<WebSocket> Connect(TestHost host, string path, Action<Microsoft.AspNetCore.Http.HttpRequest>? configure = null)
    {
        var client = host.App.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = configure;
        return client.ConnectAsync(new Uri("wss://localhost" + path), Token);
    }
    private static ValueTask Send(WebSocket socket, string text) => socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, Token);
    private static async Task<(string Text, WebSocketCloseStatus? Status)> Read(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var data = new MemoryStream(); var buffer = new byte[4096];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) return (socket.CloseStatusDescription ?? "", socket.CloseStatus);
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (Encoding.UTF8.GetString(data.ToArray()), null);
    }
    private static async Task Close(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        if (socket.State == WebSocketState.CloseReceived) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
        else if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
    }
}

[Socket("/ws/echo")]
public sealed class EchoSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(message, token);
}
[Socket("/ws/echo")]
public sealed class ConflictingSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(message, token);
}
[Socket("/ws/rooms/{room}")]
public sealed class RoomSocket : SocketHandler<string>
{
    public override ValueTask OnConnectedAsync(SocketConnection<string> connection, CancellationToken token)
    { connection.Join(connection.Route("room")!); return ValueTask.CompletedTask; }
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.BroadcastAsync(message, connection.Route("room"), token);
}
public sealed class SocketInput
{
    [Required] public string Name { get; set; } = "";
    [Required] public AddressInput Address { get; set; } = new();
}
[Socket("/ws/typed"), Tag("Realtime"), Summary("Typed socket with validation")]
public sealed class TypedSocket : SocketHandler<SocketInput, ProductResult>
{
    public override ValueTask OnMessageAsync(SocketInput message, SocketConnection<ProductResult> connection, CancellationToken token) =>
        // Distinct outgoing DTO tests that the input/output contracts are not accidentally interchangeable.
        connection.SendAsync(new ProductResult(message.Name.Length, 1), token);
}
public sealed class SocketValidator : IRequestValidator<SocketInput>
{
    public async ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(SocketInput model, CancellationToken token)
    { await Task.Delay(1, token); return model.Name == "reserved" ? [new("Name", "Name is reserved")] : []; }
}
[Socket("/ws/admin"), Authorize(Roles = "Admin")]
public sealed class AdminSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(message, token);
}
[Socket("/ws/failing")]
public sealed class FailingSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => throw new InvalidOperationException("secret");
}
[Socket("/ws/closing")]
public sealed class ClosingSocket : SocketHandler<string>
{
    public override ValueTask OnConnectedAsync(SocketConnection<string> connection, CancellationToken token) => connection.CloseAsync("bye", token);
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => ValueTask.CompletedTask;
}
public sealed class SocketLifecycle
{
    public int Connected, Disconnected, DisposeCount;
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
[Socket("/ws/lifecycle")]
public sealed class LifecycleSocket(SocketLifecycle state) : SocketHandler<string>, IAsyncDisposable
{
    public override ValueTask OnConnectedAsync(SocketConnection<string> connection, CancellationToken token)
    { Interlocked.Increment(ref state.Connected); return connection.SendAsync(connection.Id, token); }
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => ValueTask.CompletedTask;
    public override ValueTask OnDisconnectedAsync(SocketConnection<string> connection, SocketDisconnect disconnect, CancellationToken token)
    { Interlocked.Increment(ref state.Disconnected); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync()
    { if (Interlocked.Increment(ref state.DisposeCount) == 2) state.Disposed.TrySetResult(); return ValueTask.CompletedTask; }
}

[Socket("/ws/user"), Authorize]
public sealed class UserSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(connection.User.Identity!.Name!, token);
}

[Socket("/ws/business")]
public sealed class BusinessSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) =>
        message == "conflict" ? throw new HttpError(409, "Already exists") : connection.SendAsync(message, token);
}
[Socket("/ws/oversized")]
public sealed class OversizedSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) =>
        connection.BroadcastAsync(message == "oversized" ? new string('x', 257) : message, cancellationToken: token);
}
[Socket("/ws/optional/{room?}")]
public sealed class OptionalSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) =>
        connection.SendAsync((connection.Route("room") ?? "lobby") + ":" + message, token);
}

[Socket("/ws/ready-rooms/{room}")]
public sealed class ReadyRoomSocket : SocketHandler<string>
{
    public override async ValueTask OnConnectedAsync(SocketConnection<string> connection, CancellationToken token)
    {
        connection.Join(connection.Route("room")!);
        await connection.SendAsync("ready", token);
    }
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) =>
        connection.BroadcastAsync(message, connection.Route("room"), token);
}
