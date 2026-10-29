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
