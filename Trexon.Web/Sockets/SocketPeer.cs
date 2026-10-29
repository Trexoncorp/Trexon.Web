using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;

namespace Trexon.Web.Sockets;

internal sealed class SocketRoom
{
    private readonly ConcurrentDictionary<string, SocketPeer> _peers = new();
    internal void Add(SocketPeer peer) => _peers.TryAdd(peer.Id, peer);
    internal void Remove(SocketPeer peer) => _peers.TryRemove(peer.Id, out _);
    internal async ValueTask BroadcastAsync(byte[] payload, string? group, CancellationToken connectionToken, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, cancellationToken);
        foreach (var peer in _peers.Values)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!peer.IsOpen || (group != null && !peer.InGroup(group))) continue;
            try { await peer.SendAsync(payload, linked.Token); }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                linked.Token.ThrowIfCancellationRequested();
                peer.Abort(); // A failed recipient must not disconnect a healthy sender or other recipients.
            }
        }
    }
}

internal sealed class SocketPeer(WebSocket socket, HttpContext context, SocketOptions options, CancellationToken token)
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly HashSet<string> _groups = new(StringComparer.Ordinal);
    private int _closing;
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal HttpContext Context { get; } = context;
    internal CancellationToken Token { get; } = token;
    internal WebSocket Socket { get; } = socket;
    internal bool IsOpen => Volatile.Read(ref _closing) == 0 && Socket.State == WebSocketState.Open;
    internal WebSocketCloseStatus? CloseStatus { get; private set; }
    internal string? CloseReason { get; private set; }
    internal bool Aborted { get; private set; }
    internal void Join(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        if (group.Length > 256) throw new ArgumentException("Group names can contain at most 256 characters.", nameof(group));
        lock (_groups)
        {
            if (_groups.Count >= 32 && !_groups.Contains(group)) throw new InvalidOperationException("A connection can join at most 32 groups.");
            _groups.Add(group);
        }
    }
    internal void Leave(string group) { lock (_groups) _groups.Remove(group); }
    internal bool InGroup(string group) { lock (_groups) return _groups.Contains(group); }
    internal void CheckPayloadSize(int length)
    {
        if (length > options.MaxMessageBytes) throw new InvalidOperationException("Outgoing socket message exceeds MaxMessageBytes.");
    }
    internal async ValueTask SendAsync(byte[] payload, CancellationToken cancellationToken)
    {
        CheckPayloadSize(payload.Length);
        using var send = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
        send.CancelAfter(options.SendTimeout);
        await _send.WaitAsync(send.Token);
        try
        {
            if (!IsOpen) throw new InvalidOperationException("The socket is closing or disconnected.");
            await Socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, send.Token);
        }
        catch (OperationCanceledException) { Abort(); throw; }
        finally { _send.Release(); }
    }
    internal async ValueTask RequestCloseAsync(WebSocketCloseStatus status, string reason, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        CloseStatus = status; CloseReason = reason;
        using var close = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        close.CancelAfter(options.CloseTimeout);
        await _send.WaitAsync(close.Token);
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await Socket.CloseOutputAsync(status, reason, close.Token);
        }
        finally { _send.Release(); }
    }
    internal void Abort()
    {
        Aborted = true;
        Interlocked.Exchange(ref _closing, 1);
        Socket.Abort();
    }
    internal async ValueTask FinishAsync()
    {
        if (Token.IsCancellationRequested || Socket.State == WebSocketState.Aborted) { Abort(); return; }
        using var close = new CancellationTokenSource(options.CloseTimeout);
        try
        {
            if (Socket.State == WebSocketState.CloseReceived)
            {
                // Empty peer close frames have no status, which must not be sent on the wire.
                var status = Socket.CloseStatus is null or WebSocketCloseStatus.Empty ? WebSocketCloseStatus.NormalClosure : Socket.CloseStatus.Value;
                await RequestCloseAsync(status, Socket.CloseStatusDescription ?? "", close.Token);
            }
            else if (Socket.State == WebSocketState.Open)
                await RequestCloseAsync(CloseStatus ?? WebSocketCloseStatus.NormalClosure, CloseReason ?? "Connection closed", close.Token);
            if (Socket.State == WebSocketState.CloseSent)
            {
                var buffer = new byte[512];
                while (Socket.State == WebSocketState.CloseSent)
                    await Socket.ReceiveAsync(buffer.AsMemory(), close.Token);
            }
            CloseStatus ??= Socket.CloseStatus;
            CloseReason ??= Socket.CloseStatusDescription;
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException) { Abort(); }
    }
}
