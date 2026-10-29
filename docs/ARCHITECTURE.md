# Архитектура и асинхронность

[Оглавление](README.md)

## На чём основана библиотека

HTTP обслуживает Kestrel. Маршруты, binding, scoped-активацию классов, DataAnnotations,
JSON-форматирование и авторизацию выполняет ASP.NET Core MVC. `[Routes]` объединяет
префикс и API-поведение; feature provider включает только типы из `AddRoutes`.
Application model convention исключает публичные методы без HTTP-атрибутов.

OpenAPI строится через ApiExplorer и Swashbuckle. Атрибуты Trexon.Web добавляют теги,
summary и security metadata. UI использует тот же документ, что `/openapi.json`.
Нового HTTP-протокола, контейнера DI, ORM и языка схем в библиотеке нет.

Исходники:

- [TrexonApp](../Trexon.Web/TrexonApp.cs): конфигурация, сборка, pipeline, lifecycle.
- [RouteDiscovery](../Trexon.Web/Internal/RouteDiscovery.cs): явная регистрация типов.
- [Documentation](../Trexon.Web/Internal/Documentation.cs): OpenAPI metadata.
- [Validation](../Trexon.Web/Validation.cs): async-валидаторы запросов.
- [Security](../Trexon.Web/Security/JwtModule.cs) и [Users](../Trexon.Web/Users/UsersModule.cs).

## Жизненный цикл

`Create → Configure/AddRoutes/AddModule → BuildAsync → InitializeAsync модулей → RunAsync`.
Builder выполняет только регистрацию. `BuildAsync` строит host, проверяет маршруты
и последовательно ждёт модули. При ошибке startup host асинхронно освобождается и
не начинает принимать запросы. Переданный токен отменяет подготовку между этапами
и поступает в I/O модулей.

В `await app.RunAsync(token)` библиотека сама владеет хостом и освобождает его после
остановки. При `await using var web = await app.BuildAsync(token)` ресурсом владеет
вызывающий код; можно использовать TestServer, StartAsync, StopAsync и стандартный host API.
Вторая сборка того же builder запрещена.

Параллельность HTTP-запросов обеспечивает ASP.NET Core. Это не фоновые задания:
планировщика, очереди заданий и worker-процессов Trexon.Web не создаёт.

## Порядок обработки запроса

1. Обёртка request logging, если включён модуль.
2. Обработчик исключений.
3. Request ID и обработка пустых error responses.
4. Routing и WebSocket transport, если есть socket handlers.
5. CORS, если подключён.
6. Authentication, затем authorization.
7. Rate limiter, затем output cache, если подключены.
8. Middleware модулей, затем пользовательские `Use` callbacks.
9. Swagger UI middleware и выполнение endpoints.
10. В MVC: binding/DataAnnotations, custom async validation, handler, formatter.

Rate limiter и output cache стоят после security: identity уже установлена.
Cookie CSRF middleware стоит перед выполнением изменяющего endpoint и также защищает
встроенный login. Пользовательский middleware не должен обходить правила безопасности.
Для особого порядка используйте стандартные возможности builder/host осознанно.

## Async-контракт

Запуск/сборка, module initialization, exception responses, validators, API key checks,
account/store операции используют Task/ValueTask и CancellationToken. Обработчики примера
возвращают Task или ValueTask. Конфигурация и работа CPU (например, выпуск JWT,
хеширование паролей, формирование документа OpenAPI) выполняются обычными методами.

В библиотеке нет `.Result`, `.Wait()`, `GetAwaiter().GetResult()` или `Task.Run` для I/O.
Не требуется превращать каждое присваивание или регистрацию DI в фиктивный async-метод.
Microsoft.Data.Sqlite не умеет настоящее async I/O; async-контракты сохраняют возможность
поставить сетевое хранилище с соответствующим драйвером. Подробности в [Users](USERS.md).

## Границы дизайна

MVC использует reflection. Native AOT и aggressive trimming не поддерживаются.
Генератор кода, hot reload модулей, DI-контейнер и discovery по всем сборкам отсутствуют.
Swagger и SQLite являются зависимостями NuGet-пакета, даже если соответствующий модуль
не включён; runtime-поведение модулей opt-in, но поставка пока одним пакетом.
Отдельные extension-пакеты можно выделить позднее без усложнения первого приложения.

## WebSocket

AddSocket регистрирует endpoint с GET/CONNECT и metadata класса. Security middleware
проверяет handshake до AcceptWebSocketAsync. Каждый handler и его DI scope живут до
отключения; pipeline ожидает обработку соединения. SocketHandler управляет receive loop,
сборкой фрагментов, JSON, MVC-валидацией и async validators. SocketPeer сериализует отправки,
ограничивает время и закрывает соединение; SocketRoom хранит изолированный реестр endpoint.
Сетевые операции и cleanup ожидаются, фоновых sender/reader jobs нет.
