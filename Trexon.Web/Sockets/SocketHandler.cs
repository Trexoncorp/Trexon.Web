using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Trexon.Web.Sockets;

/// <summary>Base registration contract. Derive from SocketHandler&lt;T&gt; or SocketHandler&lt;TIn,TOut&gt;.</summary>
public abstract class SocketHandler
{
    internal abstract ValueTask RunAsync(SocketPeer peer, SocketRoom room, SocketOptions options);
}

/// <summary>Handles messages with the same input and output type.</summary>
public abstract class SocketHandler<T> : SocketHandler<T, T>;

/// <summary>A scoped, per-connection handler with typed messages, validation, and asynchronous lifecycle callbacks.</summary>
public abstract class SocketHandler<TIn, TOut> : SocketHandler
{
    /// <summary>Runs after accepting and registering the connection. Join groups or send an initial message here.</summary>
    public virtual ValueTask OnConnectedAsync(SocketConnection<TOut> connection, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <summary>Processes one complete validated message at a time. The library owns the receive loop.</summary>
    public abstract ValueTask OnMessageAsync(TIn message, SocketConnection<TOut> connection, CancellationToken cancellationToken);
    /// <summary>Runs once after disconnect and registry removal, before the connection's DI scope is disposed.</summary>
    public virtual ValueTask OnDisconnectedAsync(SocketConnection<TOut> connection, SocketDisconnect disconnect, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    internal sealed override async ValueTask RunAsync(SocketPeer peer, SocketRoom room, SocketOptions options)
    {
        var services = peer.Context.RequestServices;
        var json = services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var connection = new SocketConnection<TOut>(peer, room, json);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        var time = services.GetRequiredService<TimeProvider>();
        var window = time.GetTimestamp(); var count = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(options.MaxMessageBytes, 4096));
        room.Add(peer);
        try
        {
            await OnConnectedAsync(connection, peer.Token);
            using var payload = new MemoryStream();
            while (peer.IsOpen && !peer.Token.IsCancellationRequested)
            {
                payload.SetLength(0);
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await peer.Socket.ReceiveAsync(buffer.AsMemory(0, Math.Min(buffer.Length, options.MaxMessageBytes)), peer.Token);
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType != WebSocketMessageType.Text)
                    { await peer.RequestCloseAsync(WebSocketCloseStatus.InvalidMessageType, "Only text messages are supported", peer.Token); return; }
                    if (payload.Length + received.Count > options.MaxMessageBytes)
                    { await peer.RequestCloseAsync(WebSocketCloseStatus.MessageTooBig, "Message exceeds MaxMessageBytes", peer.Token); return; }
                    payload.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                if (time.GetElapsedTime(window) >= TimeSpan.FromMinutes(1)) { window = time.GetTimestamp(); count = 0; }
                if (options.MessagesPerMinute > 0 && ++count > options.MessagesPerMinute)
                { await peer.RequestCloseAsync(WebSocketCloseStatus.PolicyViolation, "Message rate exceeded", peer.Token); return; }
                TIn? message;
                try
                {
                    _ = new UTF8Encoding(false, true).GetCharCount(payload.GetBuffer(), 0, (int)payload.Length);
                    message = typeof(TIn) == typeof(string)
                        ? (TIn)(object)new UTF8Encoding(false, true).GetString(payload.GetBuffer(), 0, (int)payload.Length)
                        : JsonSerializer.Deserialize<TIn>(payload.GetBuffer().AsSpan(0, (int)payload.Length), json);
                }
                catch (DecoderFallbackException)
                { await peer.RequestCloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid UTF-8 text", peer.Token); return; }
                catch (JsonException)
                { await ErrorAsync(new("invalid_json", "Invalid JSON message")); continue; }
                if (message == null) { await ErrorAsync(new("validation_error", "A non-null message is required")); continue; }
                var errors = await ValidateAsync(message, peer);
                if (errors.Count > 0) { await ErrorAsync(new("validation_error", "Message validation failed", errors)); continue; }
                try { await OnMessageAsync(message, connection, peer.Token); }
                catch (HttpError error) { await ErrorAsync(new("application_error", error.Message)); }
            }
        }
        catch (OperationCanceledException) when (peer.Token.IsCancellationRequested) { peer.Abort(); }
        catch (WebSocketException) { peer.Abort(); }
        catch (Exception error)
        {
            logger.LogError(error, "WebSocket handler failed. ConnectionId: {ConnectionId}, TraceId: {TraceId}", peer.Id, peer.Context.TraceIdentifier);
            try { await peer.RequestCloseAsync(WebSocketCloseStatus.InternalServerError, "Internal server error", CancellationToken.None); }
            catch (Exception closing) when (closing is WebSocketException or OperationCanceledException or InvalidOperationException) { peer.Abort(); }
        }
        finally
        {
            room.Remove(peer);
            await peer.FinishAsync();
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            using var cleanup = new CancellationTokenSource(options.CloseTimeout);
            try { await OnDisconnectedAsync(connection, new(peer.CloseStatus, peer.CloseReason, peer.Aborted), cleanup.Token); }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested) { }
            catch (Exception error) { logger.LogError(error, "WebSocket disconnect callback failed. ConnectionId: {ConnectionId}", peer.Id); }
        }
        ValueTask ErrorAsync(SocketError error)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new SocketErrorMessage(error, peer.Context.TraceIdentifier), json);
            return payload.Length <= options.MaxMessageBytes ? peer.SendAsync(payload, peer.Token)
                : peer.RequestCloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid message", peer.Token);
        }
    }
    private static async ValueTask<Dictionary<string, string[]>> ValidateAsync(TIn model, SocketPeer peer)
    {
        var services = peer.Context.RequestServices;
        var action = new ActionContext(peer.Context, peer.Context.GetRouteData(), new ActionDescriptor(), new ModelStateDictionary());
        services.GetRequiredService<IObjectModelValidator>().Validate(action, null, "", model!);
        var issues = action.ModelState.SelectMany(pair => pair.Value!.Errors.Select(error =>
            new ValidationIssue(pair.Key, string.IsNullOrEmpty(error.ErrorMessage) ? "Invalid value" : error.ErrorMessage))).ToList();
        if (issues.Count == 0)
            foreach (var validator in services.GetServices<IValidationRegistration>())
                issues.AddRange(await validator.ValidateAsync(model!, services, peer.Token));
        return issues.GroupBy(issue => issue.Field).ToDictionary(group => group.Key, group => group.Select(issue => issue.Message).Distinct().ToArray());
    }
}
