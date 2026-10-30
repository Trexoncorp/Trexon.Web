# Справочник публичного API

[Оглавление](README.md)

XML-комментарии всех публичных типов также включаются в сборку пакета. Здесь перечислены
точки входа; исходные сигнатуры являются источником точного контракта.

## TrexonApp — namespace Trexon.Web

| API | Назначение |
| --- | --- |
| `Create(string[]? args = null, Action<ApiOptions>? configure = null)` | Обычный builder |
| `Create(WebApplicationOptions options, Action<ApiOptions>? configure = null)` | Явные host options, в том числе тесты |
| `Builder` | Стандартный WebApplicationBuilder |
| `Services` | IServiceCollection |
| `Configuration` | ConfigurationManager |
| `Options` | ApiOptions, настраивается до BuildAsync |
| `AddRoutes<T>()` / `AddRoutes(Type)` | Явная регистрация класса `[Routes]` |
| `AddRoutes<T>(string prefix)` / `AddRoutes(Type, string prefix)` | Добавочный префикс группы; повторный вызов меняет его до сборки |
| `AddModule<T>()` / `AddModule(ITrexonModule)` | Подключение модуля |
| `AddValidator<T, TValidator>()` | Scoped async-валидатор аргумента |
| `Errors(Action<ErrorOptions>)` | Сопоставления исключений |
| `Use(Action<WebApplication>)` | Пользовательские middleware |
| `Map(Action<WebApplication>)` | Пользовательские endpoints |
| `Task<WebApplication> BuildAsync(CancellationToken)` | Сборка, проверка, async-инициализация |
| `Task RunAsync(CancellationToken)` | Сборка, запуск и освобождение host |
| `Task RunAsync(int port, CancellationToken = default)` | Сборка и запуск HTTP на `0.0.0.0:port` (все IPv4-интерфейсы) |

Все fluent-методы возвращают `TrexonApp`. После сборки регистрация запрещена; сервисы
и options не следует изменять напрямую после BuildAsync. Повторная регистрация того же
route type идемпотентна; повторная регистрация module type вызывает исключение.

## Запуск собранного WebApplication

В namespace `Trexon.Web` также доступно расширение
`Task RunAsync(this WebApplication app, int port, CancellationToken = default)`.
После `await using var web = await app.BuildAsync()` достаточно `await web.RunAsync(5080)`.

Порт задаётся от 0 до 65535; `0` выбирает свободный порт ОС. Вызов с портом запускает
HTTP на всех IPv4-интерфейсах (`0.0.0.0`) и заменяет адреса из URL/Kestrel-конфигурации. Вызывать его нужно
до старта хоста. Отмена токена и Ctrl+C завершают сервер штатно, ресурсы освобождаются.
Обычный `RunAsync()` продолжает использовать конфигурацию хоста, в том числе HTTPS.

## Атрибуты

| Атрибут | Где применяется | Эффект |
| --- | --- | --- |
| `Routes(string prefix = "")` | Класс | Префикс + API binding/validation |
| `Get/Post/Put/Patch/Delete/Head/Options(string template = "")` | Метод | HTTP route |
| `Tag(string name)` | Класс/метод, несколько допустимы | OpenAPI tag |
| `Summary(string text)` + `Description` | Класс/метод | OpenAPI описание |
| `Response(int statusCode)` | Метод | Документированный статус |
| `Response(int statusCode, Type bodyType)` | Метод | Документированный статус и схема |

Стандартные атрибуты ASP.NET Core используются напрямую: `[Authorize]`, `[AllowAnonymous]`,
`[FromQuery]`, `[FromBody]`, `[FromHeader]`, `[Produces]`, `[Consumes]`, `[OutputCache]`,
`[EnableRateLimiting]`, `[DisableRateLimiting]`. DataAnnotations находятся в
`System.ComponentModel.DataAnnotations`. Поведение стандартных атрибутов не дублируется
отдельными атрибутами Trexon.Web.

## ApiOptions

| Поле | Default |
| --- | --- |
| Prefix | `""`; общий путь приложения, включая документацию и auth |
| Title | `Trexon API` |
| Version | `v1` |
| Description | null |
| DocsPath | `/docs` |
| OpenApiPath | `/openapi.json` |
| EnableDocs | true |
| IncludeExceptionDetails | false |

Пути документации задаются до BuildAsync. Идентификатор документа генератора — `v1`,
а `Version` — отображаемая версия API. SwaggerGen настраивается через `Services.Configure<SwaggerGenOptions>`.
Встроенная WebSocket-консоль доступна по `DocsPath + "/ws"` (по умолчанию `/docs/ws`);
`EnableDocs` также управляет её доступностью.
Общий префикс и префикс группы маршрутов описаны в [Routing](ROUTING.md#префикс-роутера-и-всего-приложения).

## Контракты расширения

| Контракт | Методы |
| --- | --- |
| ITrexonModule | Configure, Use (optional), InitializeAsync (optional) |
| IRequestValidator<T> | ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(T, CancellationToken) |
| IExceptionResponder | Task WriteAsync(HttpContext, Exception) |
| IApiKeyValidator | ValueTask<ClaimsPrincipal?> ValidateAsync(string, CancellationToken) |
| ITokenIssuer | AccessToken Issue(subject, name, roles, claims) |
| IUserAccounts | RegisterAsync, AuthenticateAsync, LoginAsync, RefreshAsync, LogoutAsync |
| IUserStore | InitializeAsync, TryCreateAsync, FindByNameAsync, FindByIdAsync, SetRolesAsync, AddSessionAsync, RotateAsync, RevokeAsync, GetSessionExpiryAsync |

`ValidationIssue` — field/message; `HttpError` — status/detail/title; `ErrorOptions.Map<T>`
связывает исключение со статусом и безопасным заголовком. `RequestLog` содержит
метод, путь, статус, миллисекунды и trace ID.

## Модули

`Trexon.Web.Security`: JwtSettings, JwtModule, AuthorityJwtModule, ApiKeyModule<T>, CookieModule,
AuthSchemes, SecurityExtensions. Подключение описано в [Security](SECURITY.md).

`Trexon.Web.Users`: UsersModule, UsersOptions, IUserStore, SqliteUserStore, IUserAccounts,
DTO запросов/ответов. [Users](USERS.md) описывает сроки жизни и транзакционные гарантии.

`Trexon.Web.Modules`: CorsModule, OutputCacheModule, RateLimitModule, HealthChecksModule,
RequestLoggingModule, ServiceModuleExtensions. [Modules](MODULES.md) содержит параметры и defaults.

## WebSocket — namespace Trexon.Web.Sockets

`AddSocket<THandler>()` автоматически включает транспорт и маршрут `[Socket(path)]`.
`AddSocket<THandler>(string prefix)` дополнительно задаёт префикс группы; повторный
вызов с префиксом меняет его, а без аргумента сохраняет.
`UseWebSockets(Action<SocketOptions>)` необязателен и меняет defaults.
`SocketHandler<T>` и `SocketHandler<TIn,TOut>` имеют обязательный `OnMessageAsync`
и необязательные `OnConnectedAsync`/`OnDisconnectedAsync`.

`SocketConnection<T>`: Id, User, HttpContext, CancellationToken, Route(name), Join(group),
Leave(group), SendAsync(message, token), BroadcastAsync(message, group, token), CloseAsync(reason, token).
Сетевая работа асинхронна; членство в группах — локальные синхронные операции.
Схемы и lifecycle подробно описаны в [WebSockets](WEBSOCKETS.md).
