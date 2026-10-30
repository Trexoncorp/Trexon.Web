# Разработка и тестирование в 1.1

[Оглавление](README.md) · [Модули](MODULES.md) · [WebSocket](WEBSOCKETS.md)

## Тестовый клиент без порта

Добавьте `Trexon.Web.Testing` версии `1.1.0` в тестовый проект. Production-приложению
этот пакет не нужен: TestHost не является зависимостью `Trexon.Web`.
Пакет содержит сборки для .NET 8 и .NET 10 с соответствующей версией TestHost.

Следующий законченный пример можно запустить как обычный web/console-проект с
ссылками на Trexon.Web.Testing. Он проверяет реальные HTTP-маршруты в памяти:

<!-- verify: test-client-and-startup -->
```csharp
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web;
using Trexon.Web.Hosting;
using Trexon.Web.Security;
using Trexon.Web.Testing;

await using var api = await TrexonTest.CreateAsync(ConfigureApi);
var response = await api.Client.GetAsync("/ready");
response.EnsureSuccessStatusCode();
Console.WriteLine(await response.Content.ReadAsStringAsync());

// Эту же функцию вызывает production Program.cs перед app.RunAsync().
static void ConfigureApi(TrexonApp app)
{
    app.Services.AddSingleton<ReadyState>();
    app.AddStartup<PrepareApp>().AddRoutes<ReadyRoutes>();
}
public sealed class ReadyState { public bool Ready { get; set; } }
public sealed class PrepareApp(ReadyState state) : ITrexonStartup
{
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.Ready = true;
        return ValueTask.CompletedTask;
    }
}
[Routes("/ready")]
public sealed class ReadyRoutes(ReadyState state, ICurrentUser user)
{
    [Get] public object Get() => new { state.Ready, user.IsAuthenticated };
}
```

Ответ: `{"ready":true,"isAuthenticated":false}`. Тестовый клиент не отключает
аутентификацию, валидацию или middleware. Для защищённых запросов получите обычный
JWT через ваш issuer/login и задайте `api.Client.DefaultRequestHeaders.Authorization`.
Для cookies используйте соответствующую схему и HTTPS BaseAddress; TestServer не
выполняет TLS-handshake. Тестовый код не предназначен для публичного HTTP-хостинга.

Необязательный аргумент `overrides` выполняется после ConfigureApi и до сборки/startup.
Для замены используйте обычный `services.Replace(ServiceDescriptor.Scoped<IStore, FakeStore>())`
с namespace `Microsoft.Extensions.DependencyInjection.Extensions`. Это фрагмент регистрации:
IStore и FakeStore определяет ваше приложение. Новые singleton/scoped зависимости создаёт
и освобождает контейнер; переданные готовые экземпляры остаются ответственностью вызывающего.

`api.Services` — root provider: для scoped-зависимостей создайте `CreateAsyncScope()`.
`api.CreateWebSocketClient()` работает с тем же приложением. Закройте WebSocket перед
`DisposeAsync` тестового приложения; отмену передавайте в CreateAsync и операции клиента.
Ошибки старта освобождают уже построенный host. Повторный DisposeAsync безопасен.

## Текущий пользователь

`ICurrentUser` из `Trexon.Web.Security` зарегистрирован как scoped автоматически.
Его можно получить в конструкторе роутера, сервиса или обработчика WebSocket.

- `IsAuthenticated` — есть ли аутентифицированная identity.
- `Id` — NameIdentifier или sub; HTTP 401, если проверенного непустого ID нет.
- `Name` — имя либо null.
- `Roles`, `IsInRole(...)` — роли аутентифицированных identities с учётом их RoleClaimType.
- `Principal` — исходный ClaimsPrincipal для расширенных сценариев.

`ICurrentUser` читает результат существующей аутентификации, а не проверяет JWT самостоятельно.
`[Authorize]`, роли и политики продолжают ограничивать доступ. Анонимные claims не дают ID
или роли. Вне запроса IsAuthenticated=false, Name=null, Roles пуст; Id даёт 401.
В WebSocket identity фиксируется HTTP-handshake и автоматически не обновляется между сообщениями.

## Scoped async startup

`app.AddStartup<T>()`, где T реализует `ITrexonStartup`, регистрирует обработчик как scoped.
После InitializeAsync всех модулей startup-обработчики выполняются последовательно в порядке
регистрации. Каждый получает собственный async scope, который освобождается до возврата
BuildAsync и открытия listener. Повторная регистрация одного типа идемпотентна.

Зависимости приходят через конструктор; InitializeAsync получает токен отмены. Ошибка
останавливает запуск, называет обработчик и освобождает host; отмена сохраняется как отмена.
Не храните scoped-сервисы после завершения InitializeAsync. Обычные ITrexonModule.Configure,
Use и InitializeAsync остаются совместимыми. Конструктор модуля по-прежнему выполняется
до построения DI; для I/O используйте AddStartup или прежний InitializeAsync.

Реальные примеры: [подготовка SQLite в шаблоне](../templates/trexon-api/NotesModule.cs)
и [создание администратора в полном примере](../examples/Trexon.Web.Sample/Program.cs).

## Ошибки конфигурации при запуске

Встроенный DI проверяет граф зарегистрированных сервисов и lifetimes в любом окружении.
Роутеры и WebSocket-обработчики включены в эту проверку: пропущенная регистрация или
scoped-зависимость singleton дают ошибку BuildAsync с контекстом и подсказкой app.Services.
Также проверяются явные обязательные [FromServices], неизвестные именованные authorization
policies, конфликты маршрутов и существующие настройки модулей. Фабрики DI не вызываются
искусственно, поэтому их внутренняя логика и зависимости через IServiceProvider проверяются
при фактическом разрешении. Конструкторы HTTP-обработчиков не запускаются без запроса.

Для собственных обязательных настроек используйте стандартные AddOptions<T>(),
ValidateDataAnnotations() и ValidateOnStart(): они проверяются при запуске host.
При замене стандартного DI-контейнера настройте его собственную проверку графа и scopes.

## WebSocket: scope на сообщение

Для обычного SocketHandler<T> сохраняется scope на соединение и lifecycle callbacks.
Для короткоживущих ресурсов выберите SocketMessageHandler<T> или SocketMessageHandler<TIn,TOut>.
Регистрация остаётся прежней: `app.AddSocket<MyHandler>()`.

<!-- verify: message-scope -->
```csharp
using Microsoft.Extensions.DependencyInjection;
using Trexon.Web;
using Trexon.Web.Sockets;

var app = TrexonApp.Create(args);
app.Services.AddScoped<MessageWork>();
app.AddSocket<Echo>();
await app.RunAsync(8080);

public sealed class MessageWork
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
}
[Socket("/ws/echo")]
public sealed class Echo(MessageWork work) : SocketMessageHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token)
        => connection.SendAsync($"{work.Id}: {message}", token);
}
```

Откройте /docs/ws и отправьте два сообщения: ID будет разным. Фреймворк создаёт scope
после разбора сообщения, запускает в нём DataAnnotations/custom validators и обработчик,
затем ожидает DisposeAsync зависимостей, в том числе при ошибке. Внутри сообщения validator
и handler получают одни и те же scoped-сервисы. Сообщения одного соединения последовательны.
Не сохраняйте scoped-сервисы для фонового использования после возврата OnMessageAsync.

У SocketMessageHandler нет пользовательских OnConnected/OnDisconnected: чтобы хранить
состояние соединения или управлять членством в комнате через callbacks, используйте
SocketHandler. Транспорт, identity, connection ID, ограничения, комнаты и ошибки
сохраняются между сообщениями. Это DI scope на сообщение, а не новое соединение.

## Проект с Docker, Compose и тестами

```sh
dotnet new install Trexon.Templates::1.1.0
dotnet new trexon-api -n Orders.Api --docker --tests
cd Orders.Api
dotnet test tests/Orders.Api.Tests.csproj
docker compose up --build
```

Флаги независимы и выключены по умолчанию. Dockerfile собирает ASP.NET Core приложение,
запускает его обычным непривилегированным пользователем и слушает 0.0.0.0:8080.
Compose публикует порт 5080, сохраняет SQLite и логи в отдельных named volumes.
Swagger: http://localhost:5080/docs, WebSocket-консоль: /docs/ws/.
Без Docker APP_HOST по умолчанию 127.0.0.1. Не используйте `down -v`, если нужно сохранить данные.

Тестовый проект использует тот же NotesModule, отдельную временную SQLite-базу на тест
и TrexonTest. Изменения исходников контейнера применяются через повторную сборку.
Это стартовый публичный notes API; аутентификацию подключайте по потребностям сервиса.
