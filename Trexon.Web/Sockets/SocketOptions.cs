namespace Trexon.Web.Sockets;

/// <summary>WebSocket defaults, configured once for the application.</summary>
public sealed class SocketOptions
{
    /// <summary>Maximum UTF-8 payload size in either direction, including fragmented messages. Default 64 KiB.</summary>
    public int MaxMessageBytes { get; set; } = 64 * 1024;
    /// <summary>Maximum incoming messages per connection per minute. Zero disables the limit.</summary>
    public int MessagesPerMinute { get; set; } = 120;
    /// <summary>Transport keep-alive interval. Default two minutes; this is not an application heartbeat timeout.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Maximum time to wait for a queued send and its network write.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Maximum time for the closing handshake and the cooperative disconnect callback.</summary>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Additional trusted browser origins. Same-origin is always allowed; wildcards are rejected.</summary>
    public string[] AllowedOrigins { get; set; } = [];
    /// <summary>Allows native clients which do not send Origin. Origin is not an authentication credential.</summary>
    public bool AllowMissingOrigin { get; set; } = true;

    internal SocketOptions Snapshot()
    {
        if (MaxMessageBytes is < 1 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
        if (MessagesPerMinute < 0) throw new ArgumentOutOfRangeException(nameof(MessagesPerMinute));
        if (KeepAliveInterval < TimeSpan.Zero || KeepAliveInterval > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(KeepAliveInterval));
        if (SendTimeout <= TimeSpan.Zero || SendTimeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(SendTimeout));
        if (CloseTimeout <= TimeSpan.Zero || CloseTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(CloseTimeout));
        if (AllowedOrigins == null || AllowedOrigins.Any(origin => !TryOrigin(origin, out _)))
            throw new ArgumentException("Socket origins must be explicit HTTP(S) origins without paths, credentials, queries or fragments.", nameof(AllowedOrigins));
        return new SocketOptions
        {
            MaxMessageBytes = MaxMessageBytes,
            MessagesPerMinute = MessagesPerMinute,
            KeepAliveInterval = KeepAliveInterval,
            SendTimeout = SendTimeout,
            CloseTimeout = CloseTimeout,
            AllowedOrigins = AllowedOrigins.ToArray(),
            AllowMissingOrigin = AllowMissingOrigin
        };
    }
    internal static bool TryOrigin(string value, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        origin = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return true;
    }
}

/// <summary>Maps an explicitly registered socket handler to a WebSocket endpoint.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class SocketAttribute(string path) : Attribute
{
    /// <summary>An absolute route template, for example /ws/chat/{room}.</summary>
    public string Path { get; } = path;
}

/// <summary>A client-visible protocol or validation error; the connection normally remains open.</summary>
public sealed record SocketError(string Code, string Message, IDictionary<string, string[]>? Fields = null);

/// <summary>Reserved error message sent as JSON for both JSON and plain-text endpoints.</summary>
public sealed record SocketErrorMessage(SocketError Error, string TraceId);

/// <summary>The final connection state supplied to the disconnect callback.</summary>
public sealed record SocketDisconnect(System.Net.WebSockets.WebSocketCloseStatus? Status, string? Reason, bool Aborted);
