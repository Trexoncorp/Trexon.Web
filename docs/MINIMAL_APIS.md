# Trexon.Web и Minimal APIs

[Оглавление](README.md)

## Какую задачу решает каждый подход

Minimal APIs — часть ASP.NET Core для прямой регистрации HTTP-обработчиков. Trexon.Web —
библиотека соглашений поверх ASP.NET Core MVC: явные классы, описание через атрибуты,
готовая документация и подключения сервисных функций. Оба варианта используют Kestrel,
стандартный DI, middleware и механизмы авторизации.

| Сценарий | Minimal APIs | Trexon.Web |
| --- | --- | --- |
| Один endpoint | `MapGet` рядом с кодом запуска | Класс `[Routes]`, метод `[Get]`, `AddRoutes<T>()` |
| Группа API | `MapGroup`, extension methods, свои соглашения | Префикс/теги класса, модуль с явной регистрацией |
| Swagger UI | Настройка выбранного OpenAPI/Swagger-инструмента | `/docs` и `/openapi.json` по умолчанию |
| Binding | Стандартный binding Minimal APIs | Стандартный API-controller binding MVC |
| Валидация | Зависит от версии: в .NET 10 есть встроенная регистрация validation | DataAnnotations MVC включены сразу, свой async validator через интерфейс |
| Авторизация | Стандартные policies и metadata | Те же policies через атрибуты |
| Аутентификация в Swagger | Security definitions выбранного инструмента | Регистрируется вместе с JWT/API key/cookie модулем |
| Повторное использование | Extension methods/библиотеки | `ITrexonModule`, обычные библиотеки |
| Async | Task/ValueTask и CancellationToken | Task/ValueTask, CancellationToken, async startup модулей |
| Native AOT | Поддерживается в предусмотренных ASP.NET Core сценариях | Не поддерживается из-за MVC/reflection |
| Стоимость runtime | Зависит от приложения и включённых функций | Включает MVC и слои библиотеки |

У Minimal APIs тоже есть атрибуты, фильтры, DI, авторизация и OpenAPI-интеграции.
Отличие Trexon.Web — согласованные defaults и способ организации приложения.
Короткий синтаксис не доказывает более высокую скорость или меньший расход памяти.

## Одна операция с одинаковой моделью

Минимальный вариант Minimal APIs:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/hello/{name}", (string name) => new Greeting(name));
await app.RunAsync();
public sealed record Greeting(string Name);
```

Эквивалентный HTTP-ответ в Trexon.Web показан в [быстром старте](../README.ru.md).
У Trexon.Web больше строк для отдельного простого метода, зато маршруты описываются
тем же способом при росте сервиса, а Swagger и валидация подключены общими соглашениями.
Для сравнения более полного сервиса учитывайте одинаковые требования к документации,
валидации, обработке ошибок, auth и логам; сравнение только количества строк здесь мало полезно.

## Постепенное подключение

Необязательно переписывать все обработчики. `app.Map` принимает обычные ASP.NET Core endpoints:

<!-- verify: mixed-api -->
```csharp
using Microsoft.AspNetCore.Builder;
using Trexon.Web;

var app = TrexonApp.Create(args);
app.AddRoutes<StatusRoutes>();
app.Map(web => web.MapGet("/legacy", () => new { legacy = true }));
await app.RunAsync();

[Routes("/status")]
public sealed class StatusRoutes
{
    [Get] public ValueTask<object> Get() => ValueTask.FromResult<object>(new { status = "ok" });
}
```

Для Minimal API endpoints используйте их собственные metadata/filters и `Results.*`.
Автоматическая MVC DataAnnotations-валидация и `IRequestValidator<T>` относятся к
классам `[Routes]`; библиотека не обещает перенести их на произвольный `MapPost`.
Обычные Minimal API endpoints могут быть включены в OpenAPI через стандартный ApiExplorer
и metadata ASP.NET Core; проверяйте описание сложных случаев.

## Производительность

Измерений RPS, p95/p99, аллокаций, cold start и стоимости middleware для этой версии нет.
Прежде чем заявлять преимущество, нужен отдельный воспроизводимый benchmark с одинаковыми
маршрутами, JSON, auth, валидацией и logging в Release, без отладчика. В CI сейчас проверяется
корректность, а не сравнение скорости. Наличие MVC означает, что нельзя обещать меньшие
накладные расходы, чем у минимального обработчика ASP.NET Core.

Первичные источники: [Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis?view=aspnetcore-10.0),
[Application parts MVC](https://learn.microsoft.com/en-us/aspnet/core/mvc/advanced/app-parts?view=aspnetcore-10.0),
[Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore).
