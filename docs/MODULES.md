# Готовые и собственные модули

[Оглавление](README.md) · [Архитектура](ARCHITECTURE.md)

## Контракт

`ITrexonModule` имеет три этапа:

1. `Configure(TrexonApp app)` регистрирует DI, маршруты, настройки и другие модули.
   Здесь не выполняются сетевые запросы и работа с БД.
2. `Use(WebApplication app)` добавляет middleware. Это построение pipeline, тоже без I/O.
3. `ValueTask InitializeAsync(IServiceProvider services, CancellationToken token)` выполняет
   асинхронную подготовку до возврата из `BuildAsync` и до открытия HTTP listener.

`Use` и `InitializeAsync` имеют пустые реализации по умолчанию. Не нужно писать их,
когда модуль только добавляет маршруты. Модули инициализируются последовательно,
в порядке подключения. Зависимый модуль подключайте после его зависимости.
Один тип модуля нельзя добавить дважды: это предотвращает двойную регистрацию middleware.

<!-- verify: custom-module -->
```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web;

var app = TrexonApp.Create(args);
app.AddModule<CatalogModule>();
await app.RunAsync();

public sealed class CatalogModule : ITrexonModule
{
    public void Configure(TrexonApp app)
    {
        app.Services.AddSingleton<Catalog>();
        app.AddRoutes<CatalogRoutes>();
    }
    public void Use(WebApplication web) => web.Use(async (context, next) =>
    {
        context.Response.Headers["X-Catalog"] = "enabled";
        await next(context);
    });
    public ValueTask InitializeAsync(IServiceProvider services, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
public sealed class Catalog;
[Routes("/catalog")]
public sealed class CatalogRoutes(Catalog catalog)
{
    [Get] public ValueTask<object> List()
    {
        GC.KeepAlive(catalog);
        return ValueTask.FromResult<object>(new { items = Array.Empty<string>() });
    }
}
```

Для настроенного экземпляра: `app.AddModule(new CatalogModule())`. Можно вынести модуль
в отдельную class library и NuGet-пакет. Манифест, сканирование папок и собственный
контейнер не требуются. DI использует обычные lifetimes ASP.NET Core; DbContext нельзя
разделять между параллельными запросами.

## Готовые подключения

Нужен `using Trexon.Web.Modules;`; security и users находятся в своих namespaces.

| Модуль | Вызов | Поведение |
| --- | --- | --- |
| CORS | `UseCors("https://frontend.example")` | Явные origins, любые методы/заголовки |
| CORS с cookies | `AddModule(new CorsModule([origin], allowCredentials: true))` | Разрешён конкретный origin, не wildcard |
| Rate limit | `UseRateLimit(100, TimeSpan.FromMinutes(1))` | Fixed window, без очереди, 429 + Retry-After |
| Output cache | `UseOutputCache()` | Включает стандартный `[OutputCache(Duration = 30)]` |
| Health | `UseHealthChecks("/health")` | JSON-статусы зарегистрированных проверок |
| Request logs | `UseRequestLogging()` | ILogger: метод, путь, статус, время, request ID |
| Custom log sink | `UseRequestLogging(entry => ...)` | Адаптация к Trexon.Logging или иному приёмнику |

Rate limiter делит запросы по subject/id аутентифицированного пользователя, иначе по
RemoteIpAddress. Состояние хранится в одном процессе; это не общий лимит кластера.
Прокси должен корректно передавать IP, а приложение — доверять только известным прокси.
`[DisableRateLimiting]` работает по стандартным правилам ASP.NET Core. Дополнительные
именованные политики регистрируются через `Services.AddRateLimiter`.

Output cache применяет безопасную стандартную политику ASP.NET Core: публичные успешные
GET/HEAD без аутентификации и Set-Cookie. Авторизованные ответы по умолчанию не кешируются.
Кеш варьируется по query string; дополнительные правила задаются стандартными options.
Не отключайте ограничения для пользовательских данных без подходящего ключа кеша.

HealthChecksModule использует `Services.AddHealthChecks()`: добавьте `IHealthCheck` или
`AddCheck(...)` для зависимостей. Healthy/Degraded дают 200, Unhealthy — 503.
В JSON присутствуют названия и статусы; описание/exception зависимостей не раскрываются.
Проверки выполняются по запросу `/health`, фонового polling нет.

## Прямое расширение без модуля

`app.Services`, `app.Configuration`, `app.Builder` открывают стандартный ASP.NET Core.
`app.Use(web => web.UseMiddleware<MyMiddleware>())` добавляет middleware после security.
`app.Map(web => web.MapGet(...))` добавляет обычные Minimal API endpoints.

Модуль нужен для повторного использования группы настроек. Для одного маршрута
достаточно `AddRoutes<T>()`; не создавайте модуль только ради структуры папок.
