using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Trexon.Web.Sockets;

namespace Trexon.Web.Sample;

public sealed class ChatMessage
{
    [Required, StringLength(40)] public string Name { get; set; } = "Guest";
    [Required, StringLength(2000)] public string Text { get; set; } = "";
}
public sealed record ChatEvent(string Type, string Name, string Text, DateTimeOffset At);

[Socket("/ws/chat/{room}"), Tag("Realtime"), Summary("Public chat with isolated rooms", Description = "JSON ChatMessage → ChatEvent. Names are display labels, not authentication credentials.")]
public class ChatSocket(TimeProvider time) : SocketHandler<ChatMessage, ChatEvent>
{
    public override async ValueTask OnConnectedAsync(SocketConnection<ChatEvent> connection, CancellationToken token)
    {
        var room = connection.Route("room")!;
        if (room.Length is < 1 or > 32 || room.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        { await connection.CloseAsync("Use a room name with 1-32 letters, digits, hyphens or underscores", token); return; }
        connection.Join(room);
        await connection.SendAsync(new("connected", "System", $"Connected to {room}", time.GetUtcNow()), token);
    }
    public override ValueTask OnMessageAsync(ChatMessage message, SocketConnection<ChatEvent> connection, CancellationToken token) =>
        connection.BroadcastAsync(new("message", connection.User.Identity?.Name ?? message.Name.Trim(), message.Text.Trim(), time.GetUtcNow()), connection.Route("room"), token);
}

[Socket("/ws/private/{room}"), Authorize, Summary("Authenticated chat", Description = "The identity is checked before upgrade. Native clients use Bearer/API-key headers; browser clients sign in with cookies over HTTPS.")]
public sealed class PrivateChatSocket(TimeProvider time) : ChatSocket(time);
