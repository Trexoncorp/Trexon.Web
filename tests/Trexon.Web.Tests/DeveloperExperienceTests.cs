using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trexon.Web.Hosting;
using Trexon.Web.Security;
using Trexon.Web.Sockets;
using Trexon.Web.Testing;

namespace Trexon.Web.Tests;

public sealed class DeveloperExperienceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Test_client_uses_overrides_before_scoped_startup_and_disposes_host()
    {
        var state = new StartupState();
        await using (var api = await TrexonTest.CreateAsync(app =>
        {
            app.Services.AddSingleton(state);
            app.Services.AddScoped<StartupDependency>();
            app.Services.AddSingleton(new GreetingValue("production"));
            app.AddStartup<ScopedStartup>().AddRoutes<StartupRoutes>();
        }, services => services.Replace(ServiceDescriptor.Singleton(new GreetingValue("fake"))), Token))
        {
            Assert.True(state.Initialized);
            Assert.True(state.Disposed);
            Assert.Equal("fake", state.Value);
            Assert.Equal("fake", await api.Client.GetStringAsync("/startup", Token));
        }
    }

    [Fact]
    public async Task Startup_handlers_run_once_in_order_with_distinct_disposed_scopes()
    {
        var state = new MessageState();
        await using var api = await TrexonTest.CreateAsync(app =>
        {
            app.Services.AddSingleton(state);
            app.Services.AddScoped<MessageDependency>();
            app.AddStartup<FirstStartup>().AddStartup<FirstStartup>().AddStartup<SecondStartup>();
        }, cancellationToken: Token);
        Assert.Equal(2, state.Created);
        Assert.Equal(2, state.Disposed);
        Assert.Equal(2, state.Validated.Distinct().Count());
    }

    [Fact]
    public async Task Startup_failures_dispose_scope_and_prevent_returning_test_client()
    {
        var state = new StartupState();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TrexonTest.CreateAsync(app =>
        {
            app.Services.AddSingleton(state);
            app.Services.AddScoped<StartupDependency>();
            app.AddStartup<FailingStartup>();
        }, cancellationToken: Token));
        Assert.Contains(nameof(FailingStartup), error.Message);
        Assert.True(state.Disposed);
    }

    [Fact]
    public async Task Startup_cancellation_is_propagated_and_scope_disposed()
    {
        var state = new StartupState();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var builder = TrexonApp.Create();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(cancellation);
        builder.Services.AddScoped<StartupDependency>();
        builder.AddStartup<CancellingStartup>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(cancellation.Token));
        Assert.True(state.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_constructor_services_fail_at_build_for_routes_and_sockets(bool socket)
    {
        var builder = TrexonApp.Create();
        if (socket) builder.AddSocket<MissingSocket>(); else builder.AddRoutes<MissingRoutes>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(Token));
        Assert.Contains(nameof(IMissingService), error.ToString());
        Assert.Contains("app.Services", error.Message);
    }

    [Fact]
    public async Task Invalid_policy_and_missing_action_service_fail_at_build()
    {
        var builder = TrexonApp.Create();
        builder.UseJwt(TestHost.Jwt).AddRoutes<UnknownPolicyRoutes>();
        var policyError = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(Token));
        Assert.Contains("missing-policy", policyError.Message);
        builder = TrexonApp.Create();
        builder.AddRoutes<MissingActionServiceRoutes>();
        var serviceError = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(Token));
        Assert.Contains(nameof(IMissingService), serviceError.Message);
    }

    [Fact]
    public async Task Optional_action_services_keep_their_normal_MVC_behavior()
    {
        await using var api = await TrexonTest.CreateAsync(app => app.AddRoutes<OptionalServiceRoutes>(), cancellationToken: Token);
        Assert.Equal("optional", await api.Client.GetStringAsync("/optional-service", Token));
    }

    [Fact]
    public async Task Singleton_cannot_capture_scoped_dependencies_at_build()
    {
        var app = TrexonApp.Create();
        app.Services.AddScoped<MessageState>();
        app.Services.AddSingleton<MessageDependency>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.BuildAsync(Token));
        Assert.Contains("scoped", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Current_user_uses_real_authentication_and_anonymous_id_is_401()
    {
        await using var api = await TrexonTest.CreateAsync(app => app.UseJwt(TestHost.Jwt).AddRoutes<IdentityRoutes>(), cancellationToken: Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync("/identity", Token)).StatusCode);
        var issuer = api.Services.GetRequiredService<ITokenIssuer>();
        api.Client.DefaultRequestHeaders.Authorization = new("Bearer", issuer.Issue("42", "Ada", ["Admin"]).Token);
        var user = await api.Client.GetFromJsonAsync<IdentityView>("/identity", Token);
        Assert.Equal("42", user!.Id);
        Assert.Equal("Ada", user.Name);
        Assert.Equal(["Admin"], user.Roles);
        Assert.True(user.Admin);
    }

    [Fact]
    public void Current_user_ignores_unauthenticated_claims_and_honors_custom_role_claim_type()
    {
        var context = new DefaultHttpContext();
        var builder = TrexonApp.Create();
        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        using var scope = provider.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "forged"), new(ClaimTypes.Role, "Admin")]));
        Assert.False(user.IsAuthenticated);
        Assert.False(user.IsInRole("Admin"));
        Assert.Throws<HttpError>(() => user.Id);
        context.User.AddIdentity(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, ""), new("sub", "real"), new("roles", "Reader")], "test", "name", "roles"));
        Assert.Equal("real", user.Id);
        Assert.Equal(["Reader"], user.Roles);
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
    }

    [Fact]
    public async Task Each_socket_message_gets_fresh_dependencies_and_awaited_disposal_including_errors()
    {
        var state = new MessageState();
        await using var api = await TrexonTest.CreateAsync(app =>
        {
            app.Services.AddSingleton(state);
            app.Services.AddScoped<MessageDependency>();
            app.AddValidator<string, MessageValidator>().AddSocket<ScopedMessageSocket>();
        }, cancellationToken: Token);
        using var socket = await api.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/scoped-message"), Token);
        Assert.Equal(0, state.Created); // No message handler or message resource is created at handshake.
        var first = await Exchange(socket, "one");
        var second = await Exchange(socket, "two");
        Assert.NotEqual(first, second);
        Assert.Contains("application_error", await Exchange(socket, "fail"));
        Assert.Contains("validation_error", await Exchange(socket, "reject"));
        await Exchange(socket, "three"); // The previous message's scope is disposed before the next message.
        Assert.Equal(5, state.Created);
        Assert.True(state.Disposed >= 4);
        Assert.Equal(first, state.Validated[0]);
        Assert.Equal(second, state.Validated[1]);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Token);
        Assert.Equal(5, state.Disposed);
    }

    private static async Task<string> Exchange(WebSocket socket, string message)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, Token);
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer.AsMemory(), Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}

public sealed record GreetingValue(string Text);
public sealed class StartupState { public bool Initialized, Disposed; public string? Value; }
public sealed class StartupDependency(StartupState state) : IAsyncDisposable
{
    public async ValueTask DisposeAsync() { await Task.Yield(); state.Disposed = true; }
}
public sealed class ScopedStartup(StartupState state, StartupDependency dependency, GreetingValue value) : ITrexonStartup
{
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    { GC.KeepAlive(dependency); state.Initialized = true; state.Value = value.Text; return ValueTask.CompletedTask; }
}
public sealed class FailingStartup(StartupDependency dependency) : ITrexonStartup
{
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { GC.KeepAlive(dependency); throw new InvalidOperationException("startup failed"); }
}
public sealed class CancellingStartup(StartupDependency dependency, CancellationTokenSource cancellation) : ITrexonStartup
{
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { GC.KeepAlive(dependency); cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
}
[Routes("/startup")]
public sealed class StartupRoutes(GreetingValue value) { [Get] public string Get() => value.Text; }
public interface IMissingService;
[Routes("/missing")]
public sealed class MissingRoutes(IMissingService missing) { [Get] public string Get() { GC.KeepAlive(missing); return "unreachable"; } }
[Socket("/ws/missing")]
public sealed class MissingSocket(IMissingService missing) : SocketHandler<string>
{ public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken cancellationToken) { GC.KeepAlive(missing); return ValueTask.CompletedTask; } }
[Routes("/unknown-policy"), Authorize(Policy = "missing-policy")]
public sealed class UnknownPolicyRoutes { [Get] public string Get() => "unreachable"; }
[Routes("/missing-action")]
public sealed class MissingActionServiceRoutes { [Get] public string Get([FromServices] IMissingService missing) { GC.KeepAlive(missing); return "unreachable"; } }
[Routes("/identity")]
public sealed class IdentityRoutes(ICurrentUser user)
{ [Get] public IdentityView Get() => new(user.Id, user.Name, user.Roles, user.IsInRole("Admin")); }
public sealed record IdentityView(string Id, string? Name, IReadOnlyList<string> Roles, bool Admin);
public sealed class MessageState { public int Created, Disposed; public List<string> Validated { get; } = []; }
public sealed class MessageDependency : IAsyncDisposable
{
    private readonly MessageState _state;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public MessageDependency(MessageState state) { _state = state; Interlocked.Increment(ref state.Created); }
    public async ValueTask DisposeAsync() { await Task.Yield(); Interlocked.Increment(ref _state.Disposed); }
}
[Socket("/ws/scoped-message")]
public sealed class ScopedMessageSocket(MessageDependency dependency) : SocketMessageHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken cancellationToken)
        => message == "fail" ? throw new HttpError(400, "Expected failure") : connection.SendAsync(dependency.Id, cancellationToken);
}

[Routes("/optional-service")]
public sealed class OptionalServiceRoutes { [Get] public string Get([FromServices] IMissingService? missing = null) => missing == null ? "optional" : "provided"; }
public sealed class MessageValidator(MessageDependency dependency, MessageState state) : IRequestValidator<string>
{
    public ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(string value, CancellationToken cancellationToken = default)
    {
        state.Validated.Add(dependency.Id);
        return ValueTask.FromResult<IEnumerable<ValidationIssue>>(value == "reject" ? [new("", "Rejected")] : []);
    }
}

public sealed class FirstStartup(MessageDependency dependency, MessageState state) : ITrexonStartup
{
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { state.Validated.Add(dependency.Id); return ValueTask.CompletedTask; }
}
public sealed class SecondStartup(MessageDependency dependency, MessageState state) : ITrexonStartup
{
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (state.Disposed != 1) throw new InvalidOperationException("Previous startup scope is still alive");
        state.Validated.Add(dependency.Id);
        return ValueTask.CompletedTask;
    }
}
