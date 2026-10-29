# WebSocket без ручного receive loop

[Оглавление](README.md) · [Полный пример чата](../examples/Trexon.Web.Sample/README.md)

## Минимальный сервер целиком

<!-- verify: socket-echo -->
```csharp
using Trexon.Web;
using Trexon.Web.Sockets;

var app = TrexonApp.Create(args);
app.AddSocket<EchoSocket>();
await app.RunAsync();

[Socket("/ws/echo")]
public sealed class EchoSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) =>
        connection.SendAsync(message, token);
}
```

Одно `AddSocket<T>()` подключает endpoint и WebSocket middleware. Отдельный `UseWebSockets`
не нужен. `SocketHandler<string>` получает и отправляет обычный UTF-8 текст. Другие типы
автоматически сериализуются в JSON с теми же настройками, что HTTP-модели приложения.
Обработчик получает только полное сообщение, даже если клиент прислал несколько фрагментов.

Запустите приложение с `--urls http://localhost:5080`. Клиент в консоли браузера того же origin:

```javascript
const socket = new WebSocket("ws://localhost:5080/ws/echo");
socket.onopen = () => socket.send("Привет!");
socket.onmessage = event => console.log(event.data);
// Когда закончите: socket.close(1000, "Finished");
```

В браузере безопаснее открыть готовый клиент `http://localhost:5080/chat` из полного
примера: он показывает подключения, ошибки и рассылку между вкладками.

## Типизированные сообщения и комнаты

<!-- verify: socket-rooms -->
```csharp
using System.ComponentModel.DataAnnotations;
using Trexon.Web;
using Trexon.Web.Sockets;

var app = TrexonApp.Create(args);
app.AddSocket<ChatSocket>();
await app.RunAsync();

public sealed class ChatMessage
{
    [Required, StringLength(2000)] public string Text { get; set; } = "";
}
public sealed record ChatReply(string Sender, string Text);

[Socket("/ws/chat/{room}"), Tag("Realtime"), Summary("Chat by room")]
public sealed class ChatSocket : SocketHandler<ChatMessage, ChatReply>
{
    public override ValueTask OnConnectedAsync(SocketConnection<ChatReply> connection, CancellationToken token)
    {
        connection.Join(connection.Route("room")!);
        return ValueTask.CompletedTask;
    }
    public override ValueTask OnMessageAsync(ChatMessage message, SocketConnection<ChatReply> connection, CancellationToken token) =>
        connection.BroadcastAsync(new(connection.Id, message.Text), connection.Route("room"), token);
}
```

Вход: `{"text":"Hello"}`. Выход: `{"sender":"...","text":"Hello"}`.
`SocketHandler<TIn,TOut>` позволяет иметь разные контракты запроса и ответа.
Класс создаётся через scoped DI один раз на соединение; сервисы передаются через конструктор.
Не держите долгоживущий DbContext с tracking на всё соединение: для операций с данными
открывайте короткую сессию на сообщение через factory или Trexon.Data.Open().
Регистрация и транспорт не требуют reflection на каждом сообщении.

`Join(group)` и `Leave(group)` меняют только локальное членство, поэтому синхронны.
`BroadcastAsync(message, group, token)` отправляет участникам группы, включая отправителя.
Без group рассылка идёт всем подключённым клиентам **этого endpoint**. Совпавшие имена
групп и типы сообщений у разных обработчиков не объединяют их комнаты.
Членство удаляется автоматически при отключении. До 32 групп на соединение, имя до 256 символов.

Рассылка ждёт сетевую отправку каждому получателю последовательно. Медленный клиент
ограничен SendTimeout; неисправный получатель отключается и не обрывает рассылку остальным.
Параллельные отправки в одно соединение сериализуются библиотекой. Не требуется блокировка
в обработчике и не создаётся неограниченная очередь фоновых отправок.
Комнаты локальны процессу; распределённый backplane и постоянная история не включены.

## Lifecycle и async

- `OnConnectedAsync(connection, token)` — после успешного upgrade: вступить в группы,
  отправить начальные данные.
- `OnMessageAsync(message, connection, token)` — последовательно, после валидации.
- `OnDisconnectedAsync(connection, disconnect, token)` — один раз, после удаления из
  реестра, до освобождения request scope. Можно освободить ресурсы или записать событие.

Оба дополнительных callback имеют пустую реализацию. В минимальном приложении нужен
только `OnMessageAsync`. Все I/O-операции используют ValueTask/Task и CancellationToken.
Запрос остаётся активным всё время соединения. Токен объединяет RequestAborted и остановку
хоста, поэтому idle-сокет отменяется при shutdown, даже если клиент ничего не присылает.
Для cleanup передаётся отдельный ограниченный по времени токен. Callback должен уважать
отмену: библиотека не оставляет его незавершённой фоновой задачей и не принудительно прерывает код.

`CloseAsync(reason, token)` запрашивает обычное закрытие; библиотека завершает handshake.
`SocketDisconnect` содержит Status, Reason и Aborted. При оборванном транспорте статус может
отсутствовать. Свойства connection: Id, User, HttpContext, CancellationToken; `Route(name)`
читает параметр маршрута. HttpContext оставлен для нестандартной интеграции.

## Валидация и ошибки

DataAnnotations проверяются рекурсивно до OnMessageAsync. `AddValidator<T,TValidator>()`
работает и для WebSocket-моделей: те же scoped async-валидаторы, что для HTTP, без нового
интерфейса. Пользовательские валидаторы выполняются после DataAnnotations.
Произвольный business validator не превращается автоматически в JSON Schema.

Неверный JSON, null вместо модели и ошибки валидации не разрывают соединение:

```json
{
  "error": {
    "code": "validation_error",
    "message": "Message validation failed",
    "fields": { "Text": ["The Text field is required."] }
  },
  "traceId": "..."
}
```

Оболочка `SocketErrorMessage` зарезервирована для ошибок и всегда является JSON,
в том числе у текстового handler. `invalid_json` обозначает неверный JSON;
`application_error` — безопасное сообщение намеренного HttpError. Непредвиденное исключение
логируется без отправки подробностей клиенту, затем соединение закрывается с 1011.
HTTP exception mappings не меняют уже установленный WebSocket-протокол.
Если настроенный лимит настолько мал, что даже error envelope не помещается, вместо
JSON-ошибки отправляется close 1008.

| Ситуация | Результат |
| --- | --- |
| Обычный HTTP-запрос вместо upgrade | HTTP 400 Problem Details |
| Нет auth / не хватает прав | HTTP 401 / 403 до upgrade |
| Запрещённый Origin | HTTP 403 до upgrade |
| Неверный JSON или модель | JSON error, можно прислать исправленное сообщение |
| Binary frame | Close 1003 |
| Неверный UTF-8 | Close 1007 |
| Слишком большое полное/фрагментированное сообщение | Close 1009 |
| Превышение частоты сообщений | Close 1008 |
| Неожиданное исключение обработчика | Close 1011 без внутренних подробностей |
| Ошибка сети, отмена, превышение времени закрытия | Abort/закрытие транспорта |

Не добавляйте HTTP-ответ после upgrade. Логи HTTP-запросов записывают длительность всего
соединения; тела WebSocket-сообщений автоматически не логируются.

## Аутентификация, cookies и Origin

`[Authorize]`, роли, policies и `[AllowAnonymous]` применяются к классу handler как к HTTP
endpoint. Механизм не нужно настраивать второй раз. Например, `[Authorize(Roles = "Admin")]`
вместе с `app.UseJwt(settings)` проверяется до принятия соединения.
Native ClientWebSocket может передать Authorization или X-API-Key через Options.SetRequestHeader.
Браузерный WebSocket API не позволяет выставить произвольный Authorization header:
используйте существующую cookie-сессию того же origin через HTTPS/WSS. Готовый `/chat`
показывает такой вход. JWT в query string библиотека не принимает автоматически.

CORS-политика не защищает WebSocket. По умолчанию разрешён Origin самого приложения;
дополнительные браузерные origins задаются явно. Клиенты без Origin разрешены по умолчанию
и могут быть запрещены опцией AllowMissingOrigin. Значение Origin не доказывает личность:
native-клиент может его подделать, а контроль доступа по-прежнему выполняет authentication.
Cookie-подключение защищается проверкой Origin до upgrade; HTTP antiforgery-токен для
самого upgrade не нужен. Cookie login сохраняет обычную CSRF-защиту.

Права проверяются при handshake. Действующее соединение не переаутентифицируется на каждом
сообщении; истечение JWT или изменение ролей не разрывает его автоматически. Для непрерывной
проверки добавьте правило в обработчик либо закройте соединение при изменении сессии.
GET поддерживает HTTP/1.1 upgrade, CONNECT — extended WebSocket в подходящем HTTP/2 хосте.
Прокси и его тайм-ауты должны поддерживать долгоживущие соединения.

## Настройка defaults

Только если нужны отличия:

```csharp
app.UseWebSockets(options =>
{
    options.MaxMessageBytes = 128 * 1024;
    options.MessagesPerMinute = 300;
    options.AllowedOrigins = ["https://frontend.example"];
});
```

Вызов допустим до или после AddSocket, но до BuildAsync. Настройки снимаются в snapshot
при сборке хоста. WebSocketModule можно подключить из собственного ITrexonModule.

| Опция | Default |
| --- | --- |
| MaxMessageBytes | 65536, в обе стороны, включая все фрагменты |
| MessagesPerMinute | 120 на соединение; 0 отключает лимит |
| KeepAliveInterval | 2 минуты |
| SendTimeout | 10 секунд, включая ожидание другой отправки |
| CloseTimeout | 5 секунд |
| AllowedOrigins | Дополнительных origins нет; свой origin разрешён |
| AllowMissingOrigin | true для native-клиентов |

HTTP UseRateLimit ограничивает handshake-запросы; сообщения ограничиваются отдельно.
KeepAliveInterval — transport keep-alive, а не обещание обнаружить каждый зависший клиент
за две минуты. Для прикладного контроля неактивности можно реализовать свой heartbeat.
Compression, binary streaming, durable delivery, automatic reconnect, acknowledgements,
SignalR negotiation и распределённые комнаты в этот простой API не включены.

## Документация контракта

Swagger показывает раздел WebSockets с адресами и input/output. `[Summary]` и `[Tag]`
описывают классы. В `/openapi.json` расширение `x-trexon-websockets` содержит пути,
описания, форматы text/json, авторизационные metadata и ссылки на схемы в components.schemas.
Там же присутствует SocketErrorMessage. Это расширение OpenAPI, не стандарт AsyncAPI.
HTTP Try it out не изображает WebSocket-клиент; используйте `/chat`, браузерный WebSocket
или ClientWebSocket. [Пример](../examples/Trexon.Web.Sample/Program.cs) демонстрирует
public/private комнаты на том же контракте.

Первичный источник транспортных ограничений: [ASP.NET Core WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-8.0).
