# Ошибки и трассировка

[Оглавление](README.md) · [Безопасность](SECURITY.md)

## Предсказуемый формат

Обработчик исключений включён по умолчанию. Для ошибок без собственного тела ответа
также формируются Problem Details: например, 404, 405, 401 и 403. Ответ содержит
`status`, `title`, `type`, `instance` и `traceId`. Валидационные ошибки дополнены `errors`.
Формат предназначен для клиента и не зависит от наличия Swagger.

Намеренный ответ можно сформировать через `throw new HttpError(409, "Order is already closed.")`.
Сообщение `HttpError` считается безопасным для клиента и попадает в `detail`.
Не передавайте туда SQL, пароли и исходное сообщение неизвестного исключения.

## Сопоставление исключений

<!-- verify: error-mapping -->
```csharp
using Trexon.Web;

var app = TrexonApp.Create(args);
app.AddRoutes<OrderRoutes>();
app.Errors(errors => errors.Map<OrderMissingException>(404, "Order not found"));
await app.RunAsync();

public sealed class OrderMissingException : Exception;

[Routes("/orders")]
public sealed class OrderRoutes
{
    [Get("{id:int}"), Response(404)]
    public Task<object> Get(int id, CancellationToken token) =>
        Task.FromException<object>(new OrderMissingException());
}
```

Сначала ищется mapping точного типа, затем ближайшего базового типа. Сообщение
исключения клиенту не передаётся. Неожиданные исключения дают 500; подробности
пишутся в `ILogger`. `IncludeExceptionDetails` по умолчанию false, независимо от окружения.

Если ответ уже начал отправляться, middleware не может заменить его JSON-ошибкой.
Исключение передаётся хосту, который может завершить соединение. Отмена запроса по
`RequestAborted` не преобразуется в ложную ошибку 500.

## Свой обработчик

Зарегистрируйте собственный `IExceptionResponder` в `app.Services` до `BuildAsync`.
Метод `Task WriteAsync(HttpContext context, Exception exception)` получает исходное
исключение; используйте `context.RequestAborted` при записи ответа. Scoped responder
поддерживается: middleware разрешает его из scope текущего запроса.

Готовый обработчик занимается исключениями. Если нужен другой формат ошибок binding,
настройте стандартный `ApiBehaviorOptions.InvalidModelStateResponseFactory`.
Чтобы заменить JSON для пустых 4xx/5xx, используйте стандартные middleware хоста.

## Request ID и логи

Ответ содержит `X-Request-ID`. Входной ID принимается только при длине 1–64 и ASCII
буквах, цифрах, `-`, `_`; иначе генерируется новый. В Problem Details `traceId` использует
активную Activity, когда она есть, либо request ID. Поэтому `traceId` может отличаться
от `X-Request-ID`, если включена распределённая трассировка.

Каждый завершённый HTTP-запрос автоматически записывается через `Trexon.Logging.Log.Info`:
метод, путь без query string, итоговый статус, длительность в миллисекундах и request ID.
Это включает ошибки, отклонённые запросы, попадания в кеш и страницы документации.
Для WebSocket запись появляется после завершения соединения и содержит его полную длительность.
Тела, query string, cookies и заголовки не записываются.

```text
HTTP GET /calculator/add -> 200 in 1.25ms [request-id]
```

Без настройки `Log` пишет в консоль. Настройте его один раз в приложении через `Log.Configure`:
эти же приёмники, фильтры и свойства будут применяться к HTTP-событиям. Trexon.Web не меняет
и не закрывает общий логгер; уровень записи всегда Information и учитывает фильтры `Log`.

<!-- verify: request-logging -->
```csharp
using Trexon.Logging;
using Trexon.Web;

Log.Configure(config => config.WriteTo.Console().WriteTo.File("logs/http.log"));
var web = TrexonApp.Create(args);
await web.RunAsync(5080); // Все запросы уже проходят через Log.Info.
```

Для собственного приёмника есть `UseRequestLogging(entry => ...)` и асинхронный вариант
`UseRequestLogging(async (entry, token) => ...)`. Callback заменяет стандартный вызов `Log.Info`,
поэтому запись не дублируется. Асинхронный приёмник ожидается в конвейере, без фоновых заданий;
сам `Log.Info` использует синхронный API Trexon.Logging. `UseRequestLogging()` без аргументов
оставляет стандартный приёмник. Сбой callback не прерывает HTTP-запрос и передаётся в `ILogger`.
Пример настройки общего `Log` есть в [Program.cs](../examples/Trexon.Web.Sample/Program.cs).
