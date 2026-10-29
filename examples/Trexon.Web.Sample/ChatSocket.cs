using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Trexon.Web.Sockets;

namespace Trexon.Web.Sample;

// Входящее JSON-сообщение: {"name":"Alice","text":"Hello"}.
// Trexon.Web десериализует и проверяет DataAnnotations до OnMessageAsync.
// При ошибке клиент получает JSON-конверт error и может исправить сообщение в том же соединении.
public sealed class ChatMessage
{
    // В публичном чате имя — неподтверждённая подпись, а не доказательство личности.
    [Required, StringLength(40)]
    public string Name { get; set; } = "Guest";

    [Required, StringLength(2000)]
    public string Text { get; set; } = "";
}

// Исходящий JSON: type различает служебное событие connected и сообщение message.
// At — серверное время UTC; клиенту не нужно присылать собственную метку времени.
public sealed record ChatEvent(string Type, string Name, string Text, DateTimeOffset At);

// Socket задаёт адрес подключения; room читается из пути, Tag/Summary описывают его в /docs.
// Generic-параметры задают входную и выходную модели: ручной receive-loop не требуется.
// TimeProvider приходит из DI; класс допускает наследование для защищённого варианта ниже.
[Socket("/ws/chat/{room}"), Tag("Realtime"), Summary("Public chat with isolated rooms", Description = "JSON ChatMessage → ChatEvent. Names are display labels, not authentication credentials.")]
public class ChatSocket(TimeProvider time) : SocketHandler<ChatMessage, ChatEvent>
{
    // Вызывается один раз после успешного WebSocket upgrade.
    public override async ValueTask OnConnectedAsync(SocketConnection<ChatEvent> connection, CancellationToken token)
    {
        var room = connection.Route("room")!;
        // Проверяем имя на сервере: браузерные ограничения можно обойти другим клиентом.
        if (room.Length is < 1 or > 32 || room.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            await connection.CloseAsync("Use a room name with 1-32 letters, digits, hyphens or underscores", token);
            return;
        }
        // Join меняет локальное членство в группе, поэтому не требует async.
        // Соединения и комнаты существуют в памяти процесса; история не сохраняется.
        connection.Join(room);
        // SendAsync отправляет событие только этому клиенту, не всем участникам комнаты.
        await connection.SendAsync(new("connected", "System", $"Connected to {room}", time.GetUtcNow()), token);
    }

    // Вызывается для каждого валидного сообщения; внутри одного соединения вызовы последовательны.
    // Имя вошедшего аккаунта берём из identity, а публичную подпись — из входной модели.
    // BroadcastAsync отправляет событие всей комнате, включая отправителя, и принимает токен отмены.
    public override ValueTask OnMessageAsync(ChatMessage message, SocketConnection<ChatEvent> connection, CancellationToken token) =>
        connection.BroadcastAsync(new("message", connection.User.Identity?.Name ?? message.Name.Trim(), message.Text.Trim(), time.GetUtcNow()), connection.Route("room"), token);

    // Чтение фрагментов, ограничения сообщений, отправку JSON и очистку членства при
    // отключении выполняет библиотека: для этого примера OnDisconnectedAsync не нужен.
}

// Переиспользуем весь обработчик, добавляя обязательную аутентификацию ДО upgrade.
// У public/private разные реестры: одинаковое имя комнаты не смешивает их участников.
// Браузер входит через cookie по HTTPS и передаёт её при WSS-подключении автоматически.
// Native-клиент может задать Authorization: Bearer или X-API-Key при подключении.
[Socket("/ws/private/{room}"), Authorize, Summary("Authenticated chat", Description = "The identity is checked before upgrade. Native clients use Bearer/API-key headers; browser clients sign in with cookies over HTTPS.")]
public sealed class PrivateChatSocket(TimeProvider time) : ChatSocket(time);
