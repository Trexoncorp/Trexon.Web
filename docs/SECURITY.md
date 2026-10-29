# Аутентификация и авторизация

[Оглавление](README.md) · [Пользователи](USERS.md)

Аутентификация устанавливает личность, авторизация проверяет доступ к операции.
Trexon.Web использует стандартные authentication handlers и authorization policies
ASP.NET Core. Самодельного формата токенов и отдельного механизма ролей нет.

## Локальный JWT одной строкой

<!-- verify: jwt-auth -->
```csharp
using Microsoft.AspNetCore.Authorization;
using Trexon.Web;
using Trexon.Web.Security;

var app = TrexonApp.Create(args);
var key = app.Configuration["JWT_SIGNING_KEY"]
    ?? throw new InvalidOperationException("Set JWT_SIGNING_KEY to a random secret of at least 32 bytes.");
app.UseJwt(new JwtSettings("orders", "orders-clients", key));
app.AddRoutes<PrivateRoutes>();
await app.RunAsync();

[Routes("/private"), Authorize]
public sealed class PrivateRoutes
{
    [Get] public ValueTask<object> Get() => ValueTask.FromResult<object>(new { authenticated = true });
    [Get("public"), AllowAnonymous] public ValueTask<object> Public() => ValueTask.FromResult<object>(new { anonymous = true });
    [Get("admin"), Authorize(Roles = "Admin")] public ValueTask<object> Admin() => ValueTask.FromResult<object>(new { admin = true });
}
```

`UseJwt` одновременно регистрирует проверку подписи, issuer, audience, срока действия
и кнопку Bearer в Swagger. В Authorize вставляется только токен, без префикса `Bearer`.
Используется HS256, ключ не менее 32 UTF-8 байт, access token по умолчанию живёт 15 минут,
clock skew — 30 секунд. `JwtSettings.Lifetime` и `ClockSkew` задаются через object initializer.
Изменение настроек после регистрации не предусмотрено.

`ITokenIssuer.Issue(subject, name, roles, claims)` доступен через DI для доверенного
серверного кода. Дополнительные claims не могут переопределять `sub`, роли, issuer,
audience или срок жизни. Выпуск токенов — CPU-операция, синхронный метод здесь не блокирует I/O.
Для готового входа по паролю подключите [UsersModule](USERS.md).

## Внешний провайдер

`app.UseJwtAuthority("https://identity.example", "orders-api")` подключает стандартное
получение OIDC metadata и ключей, их обновление и проверку токенов. HTTPS обязателен.
Локального `ITokenIssuer` в этом режиме нет; вход выполняется у провайдера.
Это предпочтительный вариант при существующей системе идентификации.
Локальный и внешний Bearer используют одно имя схемы; выберите один вариант на приложение.

## Роли и политики

Используйте `[Authorize(Roles = "Admin")]` для роли. Роли через запятую в одном атрибуте
означают OR, несколько атрибутов авторизации комбинируются по правилам ASP.NET Core.
JWT хранит роли в claim `role`; cookie-модуль использует стандартный `ClaimTypes.Role`.

Собственная политика регистрируется через стандартный DI:

```csharp
app.Services.AddAuthorization(options => options.AddPolicy("reports:read",
    policy => policy.RequireClaim("permission", "reports:read")));
```

На методе: `[Authorize(Policy = "reports:read")]`. Можно использовать собственный
`AuthorizationHandler<TRequirement>`. Проверка политики выполняется сервером;
наличие кнопки Swagger не заменяет авторизацию.

401 означает отсутствие подходящей аутентификации; 403 — установленную личность,
которой недостаточно прав. `[AllowAnonymous]` явно открывает конкретную операцию.

## API keys

Реализуйте `IApiKeyValidator.ValidateAsync(string key, CancellationToken token)`:
возвращайте `ClaimsPrincipal` с аутентифицированной `ClaimsIdentity` для действительного
ключа и null для неверного/отозванного. Проверка может обращаться к асинхронному хранилищу.

`app.UseApiKeys<MyValidator>()` регистрирует scoped-валидатор и Swagger-схему `ApiKey`.
Заголовок по умолчанию `X-API-Key`, имя можно передать параметром. Получение ключа
из query string не поддерживается: это уменьшает вероятность попадания ключей в URL-логи.
Хранение, срок действия и отзыв определяет валидатор приложения. В полном примере
ключ сравнивается через фиксированное время сравнения SHA-256-хешей.

Для маршрута только с ключом:
`[Authorize(AuthenticationSchemes = AuthSchemes.ApiKey)]`.
Для маршрута только с JWT: `AuthSchemes.Bearer`.

Если модулей несколько, обычный `[Authorize]` выбирает Bearer при заголовке Bearer,
затем API key при его заголовке, затем cookies, затем первую подключённую схему.
Неверный явно переданный Bearer не заменяется валидной cookie автоматически.
В Swagger обычная операция с `[Authorize]` показывает альтернативные схемы; конкретное
`AuthenticationSchemes` ограничивает их. Сложная логика policies/AND не кодируется
полностью в OpenAPI; документируйте такие требования текстом.

## Cookies и CSRF

`app.UseCookies()` подключает HTTP-only cookie с `Secure`, `SameSite=Lax`, сроком 8 часов
и sliding expiration. Вместо редиректа на HTML login возвращаются API-ответы 401/403.
Модуль самостоятельно не создаёт парольный login: его предоставляет UsersModule либо
собственный обработчик с `HttpContext.SignInAsync(AuthSchemes.Cookie, principal)`.

Для встроенных cookie-endpoints нужен HTTPS:

1. `GET /auth/cookie/csrf` устанавливает antiforgery-cookie и возвращает `requestToken`.
2. `POST /auth/cookie/login` получает username/password, cookie из первого шага и
   `X-CSRF-Token: <requestToken>`.
3. После входа снова получите `/auth/cookie/csrf`: request token связан с текущей личностью.
4. Для POST/PUT/PATCH/DELETE с cookie-сессией отправляйте актуальный `X-CSRF-Token`.
5. `POST /auth/cookie/logout` также требует этот токен.

Для Bearer/API key без cookie-аутентифицированной identity CSRF-токен не нужен.
На всех изменяющих запросах с cookie-identity CSRF проверяет middleware.
Встроенный cookie login защищён даже до появления сессии. Для собственного login
включите стандартную antiforgery-проверку самостоятельно до `SignInAsync`.
Вход без HTTPS встроенный обработчик отклоняет.

Swagger UI отправляет уже установленную браузером cookie того же origin. JavaScript
не может вручную выставить HTTP-only cookie через Authorize; для входа используйте
cookie-endpoint и передайте CSRF-заголовок. JWT и API keys вводятся напрямую в Authorize.
Для cookie-запросов с CSRF в Swagger используйте доступный браузерный клиент либо свой
Swagger request interceptor; автоматическая подстановка CSRF в UI в эту версию не включена.

Данные защиты cookies хранятся стандартным ASP.NET Core Data Protection. При нескольких
репликах потребуется общий key ring; см. [эксплуатацию](OPERATIONS.md).

Источники контрактов: [ASP.NET Core JWT](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-8.0),
[ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-8.0).

## WebSocket endpoints

На классах `[Socket]` работают те же `[Authorize]`, роли и policies. Для браузерных
cookies транспорт отдельно проверяет Origin; CORS не является защитой WebSocket.
Рекомендованный браузерный поток — cookie login по HTTPS, затем WSS того же origin.
[Полный контракт и ограничения](WEBSOCKETS.md).
