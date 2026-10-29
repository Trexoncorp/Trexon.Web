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

`UseRequestLogging()` пишет метод, путь без query string, статус, длительность и request ID.
Тела, cookies и заголовки аутентификации автоматически не логируются. Сбой пользовательского
логирующего callback не прерывает HTTP-запрос. Для экспорта в Trexon.Logging передайте callback,
как в [Program.cs примера](../examples/Trexon.Web.Sample/Program.cs).
