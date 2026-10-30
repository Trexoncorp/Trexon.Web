# Маршруты, параметры и ответы

[Оглавление](README.md) · [Валидация](VALIDATION.md)

## Классы маршрутов

Публичный конкретный класс помечается `[Routes("/products")]` и подключается через
`app.AddRoutes<ProductsRoutes>()`. Наследование не нужно. Экземпляры создаёт MVC для
каждого запроса; зависимости можно получать через конструктор.

Поддерживаются `[Get]`, `[Post]`, `[Put]`, `[Patch]`, `[Delete]`, `[Head]`, `[Options]`.
Можно использовать стандартные `[HttpGet]` и другие HTTP-атрибуты ASP.NET Core.
Только методы с HTTP-атрибутом становятся обработчиками. Вспомогательный публичный
метод без атрибута не публикуется. Обработчики должны быть экземплярными, без generic,
`ref` и `out`. Класс должен содержать хотя бы один обработчик.

```csharp
[Routes("/products"), Tag("Products")]
public sealed class ProductsRoutes(ProductService service)
{
    [Get("{id:int}"), Summary("Read a product"), Response(404)]
    public Task<ProductView> Get(int id, CancellationToken token) => service.GetAsync(id, token);
}
```

Это фрагмент: `ProductService` и `ProductView` принадлежат приложению. Полный вариант
с DI и хранилищем находится в [примере](../examples/Trexon.Web.Sample/Program.cs).
Префикс класса и относительный путь метода объединяются. Как и в MVC, путь метода,
начинающийся с `/` или `~/`, заменяет префикс; используйте это осознанно.

## Префикс роутера и всего приложения

С версии `1.0.1` можно добавить префикс при подключении класса:
`app.AddRoutes<ProductsRoutes>("/v1")`. При `[Routes("/products")]` адресом станет
`/v1/products`, а документация останется на `/docs`. Для сокетов тот же подход:
`app.AddSocket<ChatSocket>("/v1")` добавляет `/v1` к пути `[Socket]`.

`api.Prefix` переносит **всё приложение**, включая HTTP, WebSocket, auth, health checks,
Minimal API из `Map`, Swagger и консоль. Полный пример:

<!-- verify: route-prefixes -->
```csharp
using Trexon.Web;
using Trexon.Web.Sockets;

var app = TrexonApp.Create(args, api => api.Prefix = "/service");
app.AddRoutes<UsersRoutes>("/v1");
app.AddSocket<EchoSocket>("/v1");
await app.RunAsync(5080);

[Routes("/users"), Tag("Users")]
public sealed class UsersRoutes
{
    [Get("{id:int}")]
    public ValueTask<UserView> Get(int id) => ValueTask.FromResult(new UserView(id));
}

[Socket("/ws/echo")]
public sealed class EchoSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) =>
        connection.SendAsync(message, token);
}

public sealed record UserView(int Id);
```

Получатся `/service/v1/users/42`, `/service/v1/ws/echo`, `/service/docs` и
`/service/docs/ws`. Без `/service` запрос вернёт 404; `/service-other` тоже не совпадёт.
Без настройки общего префикса адреса остаются прежними. `api.Version` по-прежнему
меняет подпись версии в OpenAPI, а `[Tag]` — группу документации; URL они не меняют.

Префикс задаётся до `BuildAsync`. Допускается буквальный абсолютный путь вроде `/v1`
или `/api/v1/`; завершающий `/` убирается. Пустая строка и `/` снимают префикс.
Используйте ASCII-буквы, цифры, `/`, `-`, `_`, `.`, `~`; параметры, query, fragment,
пустые сегменты `//`, отдельные `.` и `..`, percent encoding не допускаются.

Повторный `AddRoutes<T>("/v2")` **меняет** префикс уже подключённого класса,
а `AddRoutes<T>()` его сохраняет. Одновременно старый и новый адрес не публикуются.
У `AddSocket` те же правила. Для методов с абсолютным путём `/` или `~/` префикс класса
по правилам MVC заменяется, но префикс регистрации и общий `api.Prefix` сохраняются.

OpenAPI использует `servers` для общего префикса и внешнего `PathBase` прокси;
префикс роутера входит в `paths`. Swagger и WebSocket-консоль учитывают оба уровня
без повторения `/v1`. Если формируете свои ссылки или redirects, используйте MVC
`CreatedAtActionResult`/`LinkGenerator` либо учитывайте `HttpContext.Request.PathBase`:
произвольные строковые URL в теле ответа библиотека не переписывает.

## Источники параметров

| Параметр | Источник |
| --- | --- |
| Простой тип с именем из шаблона `{id}` | Route |
| Остальной простой тип | Query string |
| Сложная модель | JSON body; поведение API controller |
| `CancellationToken` | `HttpContext.RequestAborted` |
| Зависимость конструктора | DI |
| `[FromServices]` на параметре | DI |
| `[FromHeader(Name = "X-Client")]` | Заголовок |
| `[FromQuery]`, `[FromRoute]`, `[FromBody]`, `[FromForm]` | Явно выбранный источник |

Атрибуты источников берутся из `Microsoft.AspNetCore.Mvc`. Для сложного query-фильтра
пишите `[FromQuery] Filter filter`, чтобы он не считался JSON-body. Один запрос имеет
одну body-модель. Не читайте body одновременно вручную и через модель.
Параметр с default необязателен; non-nullable ссылки считаются обязательными по правилам MVC.
`/items/{id:int}` не совпадёт со строкой и даст 404. Для `/items/{id}` со входным `int id`
неверная строка совпадёт с маршрутом, но даст 400 при binding.

## Асинхронные ответы

Используйте `Task<T>` для операций с I/O и передавайте `CancellationToken` дальше.
`ValueTask<T>` удобен, когда результат часто уже вычислен. Не добавляйте `Task.Run`,
чтобы обернуть синхронную БД, и не используйте `.Result` или `.Wait()` в обработчиках.
Обычные синхронные результаты также совместимы с MVC, но пример и основной стиль
библиотеки используют async-контракты.

Объект/record/коллекция сериализуются в JSON; enum по умолчанию сериализуются строками.
Строка использует стандартный text formatter MVC. Для JSON-строки верните `JsonResult`.
Для собственного статуса используйте `IActionResult`/`ActionResult<T>`:
`new CreatedResult("/products/42", product)`, `new NoContentResult()`, `new FileContentResult(...)`.
`[Response(201, typeof(ProductView))]` описывает ответ, но не меняет фактический HTTP-статус.
`IResult`/`Results.*` используйте в Minimal API endpoints через `app.Map`, для классов
маршрутов предпочтителен контракт MVC.

## Атрибуты документации

- `[Tag("Products")]` группирует операции. Теги метода заменяют теги класса.
- `[Summary("Read product", Description = "Markdown description")]` описывает операцию.
- `[Response(200, typeof(ProductView))]` задаёт статус и модель ответа.
- Стандартные `[Produces]`, `[Consumes]`, `[ProducesResponseType]`, `[ApiExplorerSettings]`
  продолжают работать.
- Для `[Authorize]` OpenAPI показывает способы аутентификации и ответы 401/403;
  `[AllowAnonymous]` снимает требование у конкретной операции.

DataAnnotations ограничений полей попадают в JSON Schema средствами генератора.
Произвольное бизнес-правило собственного валидатора нельзя автоматически превратить
в JSON Schema: объясните его в описании операции.

## Ошибки конфигурации

При старте проверяются совпадения HTTP-метода и шаблона, включая маршруты с разными
именами одинаковых параметров. Сложные пересечения constraints, catch-all и literal
обрабатывает стандартный маршрутизатор ASP.NET Core. Это не полное доказательство
отсутствия неоднозначных маршрутов; проверяйте важные пересечения HTTP-тестами.
