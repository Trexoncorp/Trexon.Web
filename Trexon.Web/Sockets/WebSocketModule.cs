using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Swashbuckle.AspNetCore.SwaggerGen;
using Trexon.Web.Internal;

namespace Trexon.Web.Sockets;

/// <summary>Configures the WebSocket transport and explicitly connected typed handlers.</summary>
public sealed class WebSocketModule : ITrexonModule
{
    private readonly List<SocketEndpoint> _endpoints = [];
    private SocketOptions? _runtime;
    /// <summary>Defaults shared by this application's socket endpoints. Configure before BuildAsync.</summary>
    public SocketOptions Options { get; } = new();
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        if (app.Sockets != null && app.Sockets != this) throw new InvalidOperationException("WebSockets are already configured.");
        app.Sockets = this;
        app.Services.Configure<SwaggerGenOptions>(options => options.DocumentFilter<SocketDocumentation>(_endpoints));
        app.Map(web =>
        {
            foreach (var endpoint in _endpoints)
                web.MapMethods(endpoint.Path, ["GET", "CONNECT"], context => HandleAsync(context, endpoint, web.Lifetime.ApplicationStopping))
                    .WithMetadata(endpoint.HandlerType.GetCustomAttributes(true)).ExcludeFromDescription();
        });
    }
    internal void Add<THandler>(TrexonApp app) where THandler : SocketHandler
    {
        var type = typeof(THandler);
        if (_endpoints.Any(endpoint => endpoint.HandlerType == type)) return;
        var path = type.GetCustomAttribute<SocketAttribute>()?.Path;
        if (!type.IsVisible || type.IsAbstract || type.ContainsGenericParameters || string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('#'))
            throw new ArgumentException("Socket handlers must be public concrete classes with [Socket(\"/absolute/path\")].");
        var contract = type;
        while (contract != null && (!contract.IsGenericType || contract.GetGenericTypeDefinition() != typeof(SocketHandler<,>))) contract = contract.BaseType;
        if (contract == null) throw new ArgumentException("Derive a socket handler from SocketHandler<T> or SocketHandler<TIn,TOut>.");
        app.Services.AddScoped<THandler>();
        _endpoints.Add(new(type, path, contract.GenericTypeArguments[0], contract.GenericTypeArguments[1], new SocketRoom()));
    }
    internal void UseTransport(WebApplication app)
    {
        _runtime = Options.Snapshot();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = _runtime.KeepAliveInterval });
    }
    private async Task HandleAsync(HttpContext context, SocketEndpoint endpoint, CancellationToken stopping)
    {
        if (!context.WebSockets.IsWebSocketRequest) { await Problems.WriteAsync(context, 400, "A WebSocket upgrade is required"); return; }
        var options = _runtime!;
        var originHeader = context.Request.Headers.Origin;
        var allowed = originHeader.Count == 0 ? options.AllowMissingOrigin : originHeader.Count == 1 &&
            SocketOptions.TryOrigin(originHeader[0]!, out var origin) &&
            (SocketOptions.TryOrigin(context.Request.Scheme + "://" + context.Request.Host, out var own) && string.Equals(origin, own, StringComparison.OrdinalIgnoreCase) ||
             options.AllowedOrigins.Any(value => SocketOptions.TryOrigin(value, out var trusted) && string.Equals(origin, trusted, StringComparison.OrdinalIgnoreCase)));
        if (!allowed) { await Problems.WriteAsync(context, 403, "WebSocket origin is not allowed"); return; }
        // Resolve first, so DI/configuration failures can still return an ordinary HTTP problem response.
        var handler = (SocketHandler)context.RequestServices.GetRequiredService(endpoint.HandlerType);
        using var socket = await context.WebSockets.AcceptWebSocketAsync(context.Features.Get<ConsoleSocketProtocol>()?.Value);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        await handler.RunAsync(new SocketPeer(socket, context, options, lifetime.Token), endpoint.Room, options);
    }
}

internal sealed record SocketEndpoint(Type HandlerType, string Path, Type InputType, Type OutputType, SocketRoom Room);

/// <summary>Registers high-level WebSocket endpoints without manual transport setup or receive loops.</summary>
public static class SocketExtensions
{
    /// <summary>Connects a typed handler using its [Socket] route and automatically enables the transport.</summary>
    public static TrexonApp AddSocket<THandler>(this TrexonApp app) where THandler : SocketHandler
    {
        app.EnsureMutable();
        GetModule(app).Add<THandler>(app);
        return app;
    }
    /// <summary>Optionally customizes WebSocket defaults. AddSocket also enables the transport automatically.</summary>
    public static TrexonApp UseWebSockets(this TrexonApp app, Action<SocketOptions>? configure = null)
    {
        app.EnsureMutable();
        var module = GetModule(app);
        configure?.Invoke(module.Options);
        return app;
    }
    private static WebSocketModule GetModule(TrexonApp app)
    {
        if (app.Sockets == null) app.AddModule(new WebSocketModule());
        return app.Sockets!;
    }
}
