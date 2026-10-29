# Trexon.Web

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md)

Библиотека для небольших HTTP-сервисов на C# / .NET 8+: маршруты через атрибуты,
асинхронный запуск, автоматическая валидация, Swagger на `/docs` и готовые модули.
Основана на ASP.NET Core MVC и Kestrel. Лицензия MIT.

**Версия `1.0.0-dev`, ещё не опубликована.** До стабильного релиза публичный API может
изменяться. Преимущество в производительности над Minimal APIs не заявляется.

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

[Пошаговый разбор примера](examples/Trexon.Web.Sample/README.md) содержит команды
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
| Пользователи, SQLite или своё хранилище, refresh/logout | `UseUsers(path)` |
| Роли и политики | Стандартный `[Authorize]` |
| CORS | `UseCors(origin)` |
| Ограничение запросов | `UseRateLimit(100)` |
| Кеш публичных ответов | `UseOutputCache()` и `[OutputCache]` |
| Проверка зависимостей | `UseHealthChecks()` |
| Логи HTTP-запросов | `UseRequestLogging()` |
| Типизированные WebSocket, комнаты и lifecycle | `AddSocket<THandler>()` |
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
между вкладками. [Руководство: WebSocket](docs/WEBSOCKETS.md).

## Подключить библиотеку

Пакет ещё не опубликован; сейчас используйте ссылку на проект:

```sh
dotnet new web -n Hello
dotnet add Hello/Hello.csproj reference /path/to/Trexon.Web/Trexon.Web/Trexon.Web.csproj
```

Замените `Hello/Program.cs` первым примером и запустите
`dotnet run --project Hello -- --urls http://localhost:5080`.
[Сборка локального NuGet-пакета](docs/PUBLISHING.md).

## Документация

Начните с [руководства](docs/README.md). В нём есть:

- [Первый сервис](docs/GETTING_STARTED.md) и [маршруты](docs/ROUTING.md).
- [Валидация](docs/VALIDATION.md) и [ошибки](docs/ERRORS.md).
- [Аутентификация и авторизация](docs/SECURITY.md), [пользователи](docs/USERS.md).
- [Свои и готовые модули](docs/MODULES.md).
- [Сравнение с Minimal APIs](docs/MINIMAL_APIS.md).
- [Архитектура и async](docs/ARCHITECTURE.md), [справочник API](docs/API.md).
- [Проверки](docs/DEVELOPMENT.md), [эксплуатация](docs/OPERATIONS.md), [границы версии](docs/ROADMAP.md).

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
