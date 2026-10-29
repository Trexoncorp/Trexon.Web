# Первый сервис

[Оглавление](README.md) · Далее: [маршруты](ROUTING.md)

## Требования и подключение

В репозитории закреплён SDK `10.0.100` с переходом на установленный feature band 10.
Библиотека и пример ориентированы на `net8.0`; тесты запускаются на .NET 8 и .NET 10.
Для запуска примера нужен ASP.NET Core Runtime 8, поставляемый в SDK 8.
Проверьте установку: `dotnet --list-sdks` и `dotnet --list-runtimes`.

Пока версия не опубликована, используйте ProjectReference:

```sh
dotnet new web -n Hello
dotnet add Hello/Hello.csproj reference /path/to/Trexon.Web/Trexon.Web/Trexon.Web.csproj
```

`Hello` остаётся обычным ASP.NET Core приложением. Trexon.Web — библиотека, собственного
CLI, процесса-менеджера и отдельного сервера установки нет.

## Программа целиком

Замените `Hello/Program.cs` примером из [README](../README.ru.md). Он объявляет маршрут
`GET /hello/{name}`, подключает его через `AddRoutes<GreetingRoutes>()` и запускает
сервер через `await app.RunAsync()`.

```sh
dotnet run --project Hello -- --urls http://localhost:5080
curl http://localhost:5080/hello/Ada
```

Ответ: `{"name":"Ada"}`. Откройте `/docs`: параметр `name`, тип ответа и описание
операции уже присутствуют. OpenAPI доступен на `/openapi.json`.

`RunAsync` завершается при остановке хоста и освобождает его ресурсы. Для явного
владения хостом используйте `await using var web = await app.BuildAsync(token)`;
затем `await web.RunAsync(token)`. Построить один экземпляр `TrexonApp` можно один раз.

## Настройки документации

<!-- verify: docs-options -->
```csharp
using Trexon.Web;

var app = TrexonApp.Create(args, api =>
{
    api.Title = "Orders API";
    api.Version = "v1";
    api.Description = "Order processing service";
    api.DocsPath = "/reference";
    api.OpenApiPath = "/schema.json";
});
await app.RunAsync();
```

Оба пути задаются до сборки приложения. `EnableDocs = false` выключает UI и JSON.
При публикации под PathBase относительная ссылка Swagger сохраняет префикс приложения;
настройте PathBase до маршрутизации через стандартные средства хоста или прокси.

## Что происходит автоматически

Подключаются MVC binding, рекурсивная DataAnnotations-валидация, JSON для объектов,
Problem Details для ошибок, request ID и описание OpenAPI. Дополнительные модули не
включаются сами: например, `[Authorize]` требует подключённого способа аутентификации.
Это проверяется при построении хоста.

Собственные классы находятся в любой папке и любой сборке. Имена с окончанием
`Controller` не требуются. Сборки не сканируются: каждый класс подключается явно,
непосредственно либо через модуль.
