namespace Trexon.Web.Sockets;

/// <summary>A typed message handler with a fresh async DI scope per message. No per-connection lifecycle callbacks.</summary>
public abstract class SocketMessageHandler<T> : SocketMessageHandler<T, T>;

/// <summary>Processes messages with fresh scoped dependencies and validators, disposed after each message.</summary>
public abstract class SocketMessageHandler<TIn, TOut> : SocketHandler<TIn, TOut>
{
    /// <inheritdoc />
    public sealed override ValueTask OnConnectedAsync(SocketConnection<TOut> connection, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    /// <inheritdoc />
    public sealed override ValueTask OnDisconnectedAsync(SocketConnection<TOut> connection, SocketDisconnect disconnect, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class MessageScopedSocket<TIn, TOut>(Type handlerType) : SocketHandler<TIn, TOut>
{
    internal override Type? MessageHandlerType => handlerType;
    public override ValueTask OnMessageAsync(TIn message, SocketConnection<TOut> connection, CancellationToken cancellationToken) => throw new InvalidOperationException("Message-scoped dispatch requires a DI scope.");
}
