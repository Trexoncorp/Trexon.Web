# Первый сервис

[Оглавление](README.md) · Далее: [маршруты](ROUTING.md)

## Требования и подключение

В репозитории закреплён SDK `10.0.100` с переходом на установленный feature band 10.
Библиотека и пример ориентированы на `net8.0`; тесты запускаются на .NET 8 и .NET 10.
Для запуска примера нужен ASP.NET Core Runtime 8, поставляемый в SDK 8.
Проверьте установку: `dotnet --list-sdks` и `dotnet --list-runtimes`.

Установите стабильный пакет из NuGet:

```sh
dotnet new web -n Hello
dotnet add Hello/Hello.csproj package Trexon.Web --version 1.0.0
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

## Запуск с указанием порта

У `TrexonApp` можно сразу вызвать `await app.RunAsync(5080)`.
Если требуется сначала собрать приложение, поддерживается тот же вызов у `WebApplication`:

<!-- verify: port-run -->
```csharp
using Trexon.Web;

var app = TrexonApp.Create(args);
await using var web = await app.BuildAsync();
await web.RunAsync(5080);
```

Откроется HTTP на `127.0.0.1:5080`; Swagger доступен по `/docs`. Явный порт имеет
приоритет над адресами URL/Kestrel-конфигурации. Для запуска по настроенным адресам,
включая HTTPS, используйте вариант без порта. Вторым аргументом можно передать токен
отмены: `await web.RunAsync(5080, cancellationToken)`.

[Калькулятор в одном файле](../examples/Trexon.Web.Calculator/README.md) показывает полный
маленький API: четыре операции, проверку параметров и готовый Swagger. Запуск:

```sh
dotnet run --project examples/Trexon.Web.Calculator
```

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
