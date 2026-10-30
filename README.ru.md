# Trexon.Web

[English](https://github.com/Trexoncorp/Trexon.Web/blob/main/README.md) · [Русский](https://github.com/Trexoncorp/Trexon.Web/blob/main/README.ru.md) · [Deutsch](https://github.com/Trexoncorp/Trexon.Web/blob/main/README.de.md)

Библиотека для небольших HTTP-сервисов на C# / .NET 8+: маршруты через атрибуты,
асинхронный запуск, автоматическая валидация, Swagger на `/docs` и готовые модули.
Основана на ASP.NET Core MVC и Kestrel. Лицензия MIT.

**Версия `1.0.1`.** [NuGet](https://www.nuget.org/packages/Trexon.Web/1.0.1) · [История изменений](https://github.com/Trexoncorp/Trexon.Web/blob/main/CHANGELOG.md). Преимущество в производительности над Minimal APIs не заявляется.

## Стартовое приложение одной командой

Пакет `Trexon.Templates` создаёт API заметок со Swagger, SQLite, валидацией,
типизированными настройками и структурированными логами:

```sh
dotnet new install Trexon.Templates
dotnet new trexon-api -n MyApi
cd MyApi
dotnet run
```

Откройте [127.0.0.1:5090/docs](http://127.0.0.1:5090/docs).
Для первого запуска не нужны `.env` и отдельный сервер БД.
[Сборка, установка локального пакета и распространение](templates/README.md).

## Первое приложение целиком

<!-- verify: first-api -->
```csharp
using Trexon.Web;

var app = TrexonApp.Create(args);
app.AddRoutes<GreetingRoutes>();
await app.RunAsync();

[Routes("/hello"), Tag("Greetings")]
public sealed class GreetingRoutes
{
    [Get("{name}"), Summary("Greet a visitor")]
    public ValueTask<Greeting> Get(string name) => ValueTask.FromResult(new Greeting(name));
}

public sealed record Greeting(string Name);
```

`GET /hello/Ada` вернёт `{"name":"Ada"}`. `/docs` позволяет вызвать методы из браузера,
`/openapi.json` содержит схему. Класс не требует наследования; подключаются только
явно зарегистрированные маршруты.

## Простой пример: калькулятор

```sh
dotnet run --project examples/Trexon.Web.Calculator
```

[Весь код — один Program.cs](https://github.com/Trexoncorp/Trexon.Web/blob/main/examples/Trexon.Web.Calculator/Program.cs): четыре операции,
валидация, JSON и готовый Swagger на [127.0.0.1:5081/docs](http://127.0.0.1:5081/docs).
Для запуска достаточно `await web.RunAsync(5081)` — как у `TrexonApp`, так и у
собранного `WebApplication`. [Разбор и запросы](https://github.com/Trexoncorp/Trexon.Web/blob/main/examples/Trexon.Web.Calculator/README.md).

## Префиксы URL

`app.AddRoutes<GreetingRoutes>("/v1")` переносит группу на `/v1/hello/{name}`,
оставляя Swagger на `/docs`. Для сокетов есть `AddSocket<T>("/v1")`.
Общий `api.Prefix = "/v1"` в `TrexonApp.Create` переносит всё приложение:
Swagger на `/v1/docs`, консоль на `/v1/docs/ws`, auth и health endpoints.
[Полный пример и управление префиксами](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/ROUTING.md#префикс-роутера-и-всего-приложения).

## Запустить полный пример

Нужны SDK 10 и ASP.NET Core Runtime 8. Из корня репозитория:

```sh
dotnet run --project examples/Trexon.Web.Sample
```

Открыть [localhost:5080/docs](http://localhost:5080/docs).

Пример — сервис задач с SQLite: регистрация, вход, JWT, ротация refresh-токенов,
роли, политики, API keys, cookie-сессии с CSRF-защитой через HTTPS, CRUD с проверкой
владельца, пользовательский валидатор, обработка ошибок, собственный модуль,
кеш, CORS, ограничение запросов, health checks. Используются
`Trexon.Configuration`, `Trexon.Logging` и `Trexon.Data` из NuGet.

[Пошаговый разбор примера](https://github.com/Trexoncorp/Trexon.Web/blob/main/examples/Trexon.Web.Sample/README.md) содержит команды
для каждого сценария, настройку администратора и HTTPS.
Без настроенного секрета пример генерирует JWT-ключ на время процесса.

## Что входит

| Возможность | Подключение |
| --- | --- |
| Маршруты, DataAnnotations, единые ошибки, request ID, Swagger | По умолчанию |
| JWT и кнопка Authorize в Swagger | `UseJwt(settings)` |
| Проверка JWT внешнего провайдера | `UseJwtAuthority(authority, audience)` |
| API keys с собственным валидатором | `UseApiKeys<TValidator>()` |
| Cookie-аутентификация и CSRF-защита | `UseCookies()` |
| Пользователи, вход по паролю в Swagger, SQLite, refresh/logout | `UseUsers(path)` |
| Роли и политики | Стандартный `[Authorize]` |
| CORS | `UseCors(origin)` |
| Ограничение запросов | `UseRateLimit(100)` |
| Кеш публичных ответов | `UseOutputCache()` и `[OutputCache]` |
| Проверка зависимостей | `UseHealthChecks()` |
| Логи HTTP-запросов через `Trexon.Logging.Log.Info` | По умолчанию; свой приёмник — `UseRequestLogging(sink)` |
| Типизированные WebSocket, комнаты, lifecycle и консоль `/docs/ws` | `AddSocket<THandler>()` |
| Свои модули | `AddModule<T>()` |

JWT на сервере и Swagger настраиваются одним вызовом. Сам ключ, issuer и audience
передаются настройками. Поддерживаются `[AllowAnonymous]`, роли и собственные политики.

## WebSocket одним обработчиком

<!-- verify: socket-echo -->
```csharp
using Trexon.Web;
using Trexon.Web.Sockets;

var app = TrexonApp.Create(args);
app.AddSocket<EchoSocket>();
await app.RunAsync();

[Socket("/ws/echo")]
public sealed class EchoSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) =>
        connection.SendAsync(message, token);
}
```

Без ручного receive loop и работы с фрагментами. Другие типы сообщений автоматически
используют JSON; `SocketHandler<TIn,TOut>` разделяет вход и выход. Авторизация и свои
валидаторы работают тем же способом, что HTTP. В примере откройте `/chat` для рассылки
между вкладками. [Руководство: WebSocket](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/WEBSOCKETS.md).

## Подключить библиотеку

Установите стабильный пакет:

```sh
dotnet new web -n Hello
dotnet add Hello/Hello.csproj package Trexon.Web --version 1.0.1
```

Замените `Hello/Program.cs` первым примером и запустите
`dotnet run --project Hello -- --urls http://localhost:5080`.
[Сборка локального NuGet-пакета](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/PUBLISHING.md).

## Документация

Начните с [руководства](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/README.md). В нём есть:

- [Первый сервис](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/GETTING_STARTED.md) и [маршруты](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/ROUTING.md).
- [Валидация](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/VALIDATION.md) и [ошибки](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/ERRORS.md).
- [Аутентификация и авторизация](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/SECURITY.md), [пользователи](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/USERS.md).
- [Свои и готовые модули](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/MODULES.md).
- [Сравнение с Minimal APIs](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/MINIMAL_APIS.md).
- [Архитектура и async](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/ARCHITECTURE.md), [справочник API](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/API.md).
- [Проверки](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/DEVELOPMENT.md), [эксплуатация](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/OPERATIONS.md), [границы версии](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/ROADMAP.md).

Операции ввода-вывода и запуск представлены async API с `CancellationToken`.
Конфигурирование и вычисления остаются синхронными. SQLite-драйвер внутри выполняет
свои async-вызовы синхронно; это ограничение провайдера, а не отдельная очередь задач.
Для сетевой БД можно реализовать асинхронный `IUserStore`. Native AOT и trimming не поддерживаются.

## Проверить

```sh
dotnet build Trexon.Web.slnx --configuration Release -warnaserror
dotnet test --solution Trexon.Web.slnx --configuration Release --no-build --no-restore
python3 scripts/check-docs.py
python3 scripts/smoke-sample.py
dotnet pack Trexon.Web/Trexon.Web.csproj --configuration Release --no-build --output artifacts
```

Тесты выполняются на .NET 8 и .NET 10. HTTP-проверка полного примера сама запускает
сервер на временном локальном порту и завершает его после проверки.
