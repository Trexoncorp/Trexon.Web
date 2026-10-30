using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Trexon.Configuration;
using Trexon.Data;
using Trexon.Logging;
using Trexon.Web;
using Trexon.Web.Modules;
using Trexon.Web.Sample;
using Trexon.Web.Security;
using Trexon.Web.Sockets;
using Trexon.Web.Users;

// 1. Состав приложения и запуск.
// Весь исполняемый пример, включая HTML/JS чата, находится в этом файле.
// Точка входа: dotnet run --project examples/Trexon.Web.Sample.
// Trexon.Configuration связывает переменные окружения с [Env] в SampleSettings ниже.
// Сам по себе файл .env здесь не загружается: переменные нужно экспортировать в окружение.
var settings = Settings.Load<SampleSettings>();
// Каталог содержит независимые SQLite-базы: accounts.db и tasks.db.
Directory.CreateDirectory(settings.DataDirectory);
// HTTP-события выводим через Trexon.Logging; using освобождает логгер при остановке.
using var logger = Logger.Create(config => config.WriteTo.Console());

// Для быстрого старта ключ можно не задавать: он будет случайным на один запуск.
// Чтобы выданные JWT оставались действительны после перезапуска, задайте TREXON_JWT_KEY.
var signingKey = string.IsNullOrWhiteSpace(settings.JwtKey)
    ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) : settings.JwtKey;

// Swagger доступен по /docs, JSON-схема — по /openapi.json без отдельной регистрации.
// Эти поля описывают API в документации; маршруты добавляются ниже.
var app = TrexonApp.Create(args, api =>
{
    api.Title = "Trexon.Web — Task service";
    api.Description = "Attribute routes, modules, validation, JWT, cookies, API keys, roles, policies, caching and health checks.";
});
// Builder и Services открыты: стандартные настройки ASP.NET Core доступны напрямую.
app.Builder.WebHost.UseUrls(settings.Url);
app.Services.AddSingleton(settings);
// Политика — именованное правило доступа. Здесь она требует Admin; её использует /demo/policy.
app.Services.AddAuthorization(options => options.AddPolicy("manage-tasks", policy => policy.RequireRole("Admin")));

// Аргументы JwtSettings: издатель, получатель и ключ подписи токенов.
// UseJwt также добавляет Bearer в окно Authorize Swagger.
app.UseJwt(new JwtSettings("trexon-sample", "trexon-sample", signingKey))
    // X-API-Key проверяет наш класс SampleApiKeys; схема тоже появляется в Swagger.
    .UseApiKeys<SampleApiKeys>()
    // Cookie-сессия для браузера: вход выполняется по HTTPS с CSRF-защитой.
    .UseCookies()
    // Готовые /auth/register, /auth/login, /auth/me, refresh, logout и cookie-вход.
    // Также добавляет логин/пароль в Authorize Swagger: токен подставляется автоматически.
    // UseUsers подключаем после UseJwt: локальным аккаунтам нужен издатель токенов.
    .UseUsers(Path.Combine(settings.DataDirectory, "accounts.db"))
    // Разрешаем HTTP-запросы фронтенда с указанного origin (схема + хост + порт).
    .UseCors(settings.FrontendOrigin)
    // Не более 100 запросов в минуту на пользователя/IP; счётчики локальны процессу.
    .UseRateLimit(100)
    // Включает механизм кеша. Конкретный маршрут включает его атрибутом [OutputCache].
    .UseOutputCache()
    // /health включает проверки, зарегистрированные модулями, в том числе SQLite задач.
    .UseHealthChecks()
    // Логируем метод, путь, статус, время и идентификатор запроса без тела и секретов.
    .UseRequestLogging(request => logger.Info("HTTP {Method} {Path} -> {StatusCode} in {ElapsedMs}ms [{TraceId}]",
        request.Method, request.Path, request.StatusCode, request.ElapsedMilliseconds, request.TraceId))
    // Наш модуль сам регистрирует хранилище, сервис, валидатор и CRUD-маршруты задач.
    .AddModule<TaskModule>()
    // Классы маршрутов и сокетов подключаем явно: их не нужно искать по всей сборке.
    .AddRoutes<DiagnosticsRoutes>()
    .AddSocket<ChatSocket>()
    .AddSocket<PrivateChatSocket>()
    // Origin для WebSocket проверяется отдельно от CORS. Свой origin разрешён по умолчанию;
    // здесь дополнительно разрешаем origin отдельного фронтенда.
    .UseWebSockets(options => options.AllowedOrigins = [settings.FrontendOrigin])
    // Доменное исключение превращается в Problem Details со статусом 404 и этим заголовком.
    .Errors(errors => errors.Map<TaskNotFoundException>(404, "Task not found"));

// При необходимости можно использовать обычные Minimal API рядом с атрибутными маршрутами.
app.Map(web =>
{
    // Служебные страницы скрываем из списка операций OpenAPI.
    web.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription();
    // HTML/JavaScript хранится в строке SamplePages.Chat в конце этого же файла.
    web.MapGet("/chat", () => Results.Content(SamplePages.Chat, "text/html; charset=utf-8"))
        .ExcludeFromDescription();
});

// BuildAsync собирает конвейер и ожидает InitializeAsync модулей, включая подготовку БД.
// await using обеспечивает асинхронное освобождение приложения и его сервисов.
await using var web = await app.BuildAsync();
// Отдельный явный шаг до приёма запросов: при заданном пароле создаём/проверяем admin.
// Эти действия завершаются последовательно; фоновые задания не запускаются.
await AdminSeed.InitializeAsync(web.Services);
logger.Info("Docs: {DocsUrl}", settings.Url.TrimEnd('/') + "/docs");
if (string.IsNullOrWhiteSpace(settings.JwtKey)) logger.Warning("JWT key is ephemeral; set TREXON_JWT_KEY to preserve access-token validity across restarts.");
// Запускаем веб-сервер и ждём остановки (например, Ctrl+C). Обработчики используют async I/O;
// сам ASP.NET Core при этом может обслуживать несколько подключений одновременно.
await web.RunAsync();

namespace Trexon.Web.Sample
{
    // 2. Настройки окружения
    // Типизированные настройки: [Env] задаёт имя переменной окружения,
    // значение свойства используется, если соответствующая переменная не задана.
    public sealed class SampleSettings
    {
        // Адрес прослушивания Kestrel. Для cookie-входа задайте HTTPS-адрес с сертификатом.
        [Env("TREXON_URL")]
        public string Url { get; init; } = "http://127.0.0.1:5080";

        // По умолчанию данные лежат рядом со сборкой, а не в текущем каталоге терминала.
        [Env("TREXON_DATA_DIR")]
        public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");

        // [Secret] помечает значение для сокрытия в диагностике Trexon.Configuration.
        // Это метаданные, а не шифрование. Пустой ключ заменяется случайным в точке входа выше.
        [Env("TREXON_JWT_KEY"), Secret]
        public string JwtKey { get; init; } = "";

        // Пустое значение отключает успешную проверку демонстрационного API key.
        [Env("TREXON_API_KEY"), Secret]
        public string ApiKey { get; init; } = "";

        // Если пароль не задан, AdminSeed не создаёт администратора.
        [Env("TREXON_ADMIN_PASSWORD"), Secret]
        public string AdminPassword { get; init; } = "";

        // Дополнительный origin фронтенда для HTTP CORS и проверки WebSocket Origin.
        [Env("TREXON_FRONTEND_ORIGIN")]
        public string FrontendOrigin { get; init; } = "http://localhost:5173";
    }

    // 3. Свой модуль, модели, валидация и хранилище
    // Модуль объединяет настройки DI, маршруты, middleware и асинхронную подготовку данных.
    // Чтобы подключить весь функционал задач, в точке входа выше достаточно AddModule<TaskModule>().
    public sealed class TaskModule : ITrexonModule
    {
        // Первый этап: описываем зависимости и функции до сборки приложения.
        public void Configure(TrexonApp app)
        {
            // Singleton — фабрика Data с конфигурацией, а не общий DbContext.
            // Каждая операция TaskService открывает отдельную сессию через data.Open().
            app.Services.AddSingleton(provider => Data.Data.Create(config => config
                .UseSqlite("Data Source=" + Path.Combine(provider.GetRequiredService<SampleSettings>().DataDirectory, "tasks.db"))
                .Entity<TaskItem>(entity =>
                {
                    entity.HasKey(task => task.Id);
                    entity.Property(task => task.Title).HasMaxLength(120);
                })));
            // Scoped-сервис создаётся на HTTP-запрос; DI передаст его в TaskRoutes.
            app.Services.AddScoped<TaskService>();
            // Пользовательская проверка дополняет DataAnnotations для тела CreateTask.
            app.AddValidator<CreateTask, TaskTitleValidator>();
            app.AddRoutes<TaskRoutes>();
            // Эта проверка будет включена в общий ответ /health.
            app.Services.AddHealthChecks().AddCheck<TaskDatabaseHealth>("tasks-sqlite");
        }

        // Второй этап: добавляем собственный middleware в HTTP-конвейер.
        public void Use(WebApplication web) => web.Use(async (context, next) =>
        {
            // Заголовок позволяет увидеть в ответах /tasks, что модуль подключён.
            if (context.Request.Path.StartsWithSegments("/tasks")) context.Response.Headers["X-Task-Module"] = "enabled";
            // Обязательно передаём запрос дальше и дожидаемся остальных обработчиков.
            await next(context);
        });

        // Третий этап: BuildAsync ожидает подготовку схемы до запуска listener.
        // Токен отмены передаём дальше, чтобы остановка могла прервать подготовку.
        public async ValueTask InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
            await services.GetRequiredService<Data.Data>().UpdateAsync(cancellationToken);
    }

    // Сущность хранилища. Её структура не обязана совпадать с публичным JSON API.
    public sealed class TaskItem
    {
        // SQLite генерирует первичный ключ при сохранении новой записи.
        public int Id { get; set; }
        // Идентификатор владельца берётся из проверенной identity, а не из тела запроса.
        public string OwnerId { get; set; } = "";
        public string Title { get; set; } = "";
        // Новая задача по умолчанию не завершена (false).
        public bool Completed { get; set; }
    }

    // Входная модель создания: проверяется автоматически до вызова метода маршрута.
    public sealed class CreateTask
    {
        // Required запрещает пустое значение, StringLength ограничивает длину строки.
        [Required, StringLength(120, MinimumLength = 3)]
        public string Title { get; set; } = "";
    }

    // Отдельная модель обновления позволяет менять только разрешённые поля.
    public sealed class UpdateTask
    {
        [Required, StringLength(120, MinimumLength = 3)]
        public string Title { get; set; } = "";
        public bool Completed { get; set; }
    }

    // Выходной DTO: OwnerId остаётся внутри сервиса и не попадает в ответ клиенту.
    public sealed record TaskView(int Id, string Title, bool Completed);

    // Точка входа выше сопоставляет это исключение с HTTP 404. Одинаковый результат для
    // отсутствующей и чужой задачи не раскрывает существование чужих записей.
    public sealed class TaskNotFoundException(int id) : Exception($"Task {id} was not found.");

    // Дополнительное правило только для CreateTask: после удаления крайних пробелов
    // должно остаться хотя бы три символа. UpdateTask здесь проверяется DataAnnotations.
    public sealed class TaskTitleValidator : IRequestValidator<CreateTask>
    {
        // Проверка вычислительная, поэтому возвращаем готовый ValueTask без Task.Run.
        // Тот же интерфейс позволяет await для проверки через БД/внешний сервис.
        public ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(CreateTask model, CancellationToken token) =>
            ValueTask.FromResult<IEnumerable<ValidationIssue>>(string.IsNullOrWhiteSpace(model.Title) || model.Title.Trim().Length < 3
                ? [new("Title", "Provide at least three non-space characters.")] : []);
    }

    // Сервис отвечает за работу с данными; HTTP-атрибуты и статусы остаются в TaskRoutes.
    // Все запросы ограничены владельцем, включая чтение, изменение и удаление по ID.
    public sealed class TaskService(Data.Data data)
    {
        public async Task<TaskView[]> List(string owner, bool? completed, int skip, int take, CancellationToken token)
        {
            // Независимая сессия освобождается асинхронно при выходе из метода.
            await using var db = data.Open();
            // Только читаем: отслеживание изменений EF Core не требуется.
            var query = db.Table<TaskItem>().AsNoTracking().Where(task => task.OwnerId == owner);
            if (completed.HasValue) query = query.Where(task => task.Completed == completed.Value);
            // LINQ описывает запрос; ToArrayAsync выполняет его. Сортировка делает пагинацию
            // определённой, а Select выбирает только поля публичного DTO.
            return await query.OrderBy(task => task.Id).Skip(skip).Take(take)
                .Select(task => new TaskView(task.Id, task.Title, task.Completed)).ToArrayAsync(token);
        }

        public async Task<TaskView> Get(string owner, int id, CancellationToken token)
        {
            await using var db = data.Open();
            // Одного совпадения ID недостаточно: запись должна принадлежать текущему аккаунту.
            var task = await db.Table<TaskItem>().AsNoTracking()
                .SingleOrDefaultAsync(task => task.Id == id && task.OwnerId == owner, token)
                ?? throw new TaskNotFoundException(id);
            return new(task.Id, task.Title, task.Completed);
        }

        public async Task<TaskView> Create(string owner, CreateTask request, CancellationToken token)
        {
            await using var db = data.Open();
            var task = new TaskItem { OwnerId = owner, Title = request.Title.Trim() };
            // Add лишь ставит запись на добавление; SaveAsync сохраняет её и заполняет Id.
            db.Add(task);
            await db.SaveAsync(token);
            return new(task.Id, task.Title, task.Completed);
        }

        public async Task<TaskView> Update(string owner, int id, UpdateTask request, CancellationToken token)
        {
            await using var db = data.Open();
            // Здесь нужен tracking: EF Core обнаружит изменения полей при SaveAsync.
            var task = await db.Table<TaskItem>()
                .SingleOrDefaultAsync(task => task.Id == id && task.OwnerId == owner, token)
                ?? throw new TaskNotFoundException(id);
            task.Title = request.Title.Trim();
            task.Completed = request.Completed;
            await db.SaveAsync(token);
            return new(task.Id, task.Title, task.Completed);
        }

        public async Task Delete(string owner, int id, CancellationToken token)
        {
            await using var db = data.Open();
            // ExecuteDeleteAsync сразу выполняет DELETE с фильтром в БД: SaveAsync не нужен.
            var deleted = await db.Table<TaskItem>().Where(task => task.Id == id && task.OwnerId == owner).ExecuteDeleteAsync(token);
            if (deleted == 0) throw new TaskNotFoundException(id);
        }
    }

    // Пример своей проверки зависимости: CanConnectAsync проверяет доступность БД,
    // но не полноту схемы или корректность всех бизнес-операций.
    public sealed class TaskDatabaseHealth(Data.Data data) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await using var db = data.Open();
            return await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
    }

    // 4. HTTP-маршруты задач
    // Routes — общий префикс, Tag — группа в Swagger, Authorize — обязательный вход
    // для всех методов класса. Право на конкретную запись проверяет TaskService.
    // Зависимости конструктора предоставляет DI; наследовать ControllerBase не требуется.
    [Routes("/tasks"), Tag("Tasks"), Authorize]
    public sealed class TaskRoutes(TaskService tasks, IHttpContextAccessor accessor)
    {
        // JWT хранит ID в sub, cookie/API key могут использовать NameIdentifier.
        // Не принимаем owner из запроса: клиент не должен выбирать чужой аккаунт.
        private string Owner => accessor.HttpContext!.User.FindFirstValue("sub")
            ?? accessor.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new HttpError(401, "An account identity is required.");

        // GET /tasks?completed=false&skip=0&take=20.
        // Простые параметры читаются из query; значения по умолчанию делают их необязательными.
        // Range проверяется до вызова метода, token связан с отменой текущего HTTP-запроса.
        [Get, Summary("List your tasks", Description = "Optional completion filter and bounded pagination. Results are scoped to the authenticated owner.")]
        public Task<TaskView[]> List(CancellationToken token, bool? completed = null, [Range(0, int.MaxValue)] int skip = 0, [Range(1, 100)] int take = 20) =>
            tasks.List(Owner, completed, skip, take, token);

        // {id:int} связывает сегмент пути с id и допускает только целочисленный маршрут.
        // Response описывает Swagger; сам 404 создаёт обработчик TaskNotFoundException.
        // Прямой возврат Task передаёт асинхронную операцию без лишнего async/await.
        [Get("{id:int}"), Summary("Get one of your tasks"), Response(404)]
        public Task<TaskView> Get(int id, CancellationToken token) => tasks.Get(Owner, id, token);

        // Сложная модель request читается из JSON и проходит встроенную/свою валидацию.
        [Post, Summary("Create a task"), Response(201, typeof(TaskView)), Response(400, typeof(ValidationProblemDetails))]
        public async Task<IActionResult> Create(CreateTask request, CancellationToken token)
        {
            var task = await tasks.Create(Owner, request, token);
            // CreatedResult задаёт реальный статус 201, JSON-тело и заголовок Location.
            return new CreatedResult($"/tasks/{task.Id}", task);
        }

        // PUT /tasks/{id}: id берётся из пути, request — из тела, token — из контекста.
        [Put("{id:int}"), Summary("Update a task"), Response(404)]
        public Task<TaskView> Update(int id, UpdateTask request, CancellationToken token) => tasks.Update(Owner, id, request, token);

        [Delete("{id:int}"), Summary("Delete a task"), Response(204), Response(404)]
        public async Task<IActionResult> Delete(int id, CancellationToken token)
        {
            // Сначала ждём удаления, затем возвращаем 204 без тела.
            await tasks.Delete(Owner, id, token);
            return new NoContentResult();
        }
    }

    // 5. Авторизация, диагностика и создание администратора
    // Небольшие независимые маршруты показывают возможности за пределами CRUD.
    // На классе нет Authorize: доступ задаётся отдельно для каждого метода.
    [Routes("/demo"), Tag("Feature tour")]
    public sealed class DiagnosticsRoutes(IHttpContextAccessor context)
    {
        // Анонимный объект автоматически сериализуется в JSON.
        // Здесь нет I/O: готовый ValueTask позволяет сохранить асинхронный контракт без Task.Run.
        [Get("public"), Summary("An anonymous JSON response")]
        public ValueTask<object> Public() => ValueTask.FromResult<object>(new { service = "Trexon.Web", docs = "/docs" });

        // Явный FromHeader меняет источник привязки: читаем X-Client вместо query.
        [Get("headers"), Summary("Bind a value from a request header")]
        public ValueTask<object> Headers([Microsoft.AspNetCore.Mvc.FromHeader(Name = "X-Client")] string client = "unknown") =>
            ValueTask.FromResult<object>(new { client });

        // UseOutputCache в точке входа выше включает механизм, а атрибут задаёт срок для маршрута.
        // Повторный анонимный запрос в течение 30 секунд получает тот же timestamp и Guid.
        // Стандартная политика не кеширует ответы аутентифицированных запросов.
        [Get("cached"), OutputCache(Duration = 30), Summary("A public response cached for 30 seconds")]
        public ValueTask<object> Cached() => ValueTask.FromResult<object>(new { generatedAt = DateTimeOffset.UtcNow, value = Guid.NewGuid() });

        // Без учётных данных — 401. После входа берём имя и роли из проверенного principal.
        [Get("whoami"), Authorize, Summary("Inspect the authenticated identity")]
        public ValueTask<object> WhoAmI() => ValueTask.FromResult<object>(new
        {
            name = context.HttpContext!.User.Identity!.Name,
            // JWT и cookie могут представлять роль разными типами claim; учитываем оба.
            roles = context.HttpContext.User.FindAll("role").Concat(context.HttpContext.User.FindAll(ClaimTypes.Role)).Select(c => c.Value)
        });

        // Аутентификация отвечает на вопрос «кто?», авторизация — «что ему разрешено?».
        // Вошедший пользователь без Admin получает 403.
        [Get("admin"), Authorize(Roles = "Admin"), Summary("Requires the Admin role")]
        public ValueTask<object> Admin() => ValueTask.FromResult<object>(new { message = "Admin access granted" });

        // Именованное правило manage-tasks зарегистрировано в точке входа выше.
        // Условия политики можно менять централизованно, сохраняя атрибут на маршруте.
        [Get("policy"), Authorize(Policy = "manage-tasks"), Summary("Requires the manage-tasks policy")]
        public ValueTask<object> Policy() => ValueTask.FromResult<object>(new { message = "Policy satisfied" });

        // Явно требуем X-API-Key: даже действительный JWT не заменяет эту схему.
        [Get("integration"), Authorize(AuthenticationSchemes = AuthSchemes.ApiKey), Summary("Requires an API key explicitly")]
        public ValueTask<object> Integration() => ValueTask.FromResult<object>(new { message = "Integration authenticated" });

        // HttpError содержит намеренную ошибку и безопасное для клиента описание.
        // Встроенный middleware сформирует Problem Details; Response лишь документирует статус.
        [Get("conflict"), Summary("A deliberate, client-safe 409 response"), Response(409)]
        public ValueTask<object> Conflict() => throw new HttpError(409, "This task is already completed.");

        // Неожиданное исключение логируется стандартным ILogger. Клиент получает общий 500
        // без текста исключения и stack trace: это можно проверить через Swagger.
        [Get("failure"), Summary("A handled unexpected exception"), Response(500)]
        public ValueTask<object> Failure() => throw new InvalidOperationException("Internal implementation details are logged, not returned.");
    }

    // Точка расширения проверки API key. Для примера используем один ключ из окружения;
    // в своём сервисе здесь можно асинхронно обращаться к хранилищу интеграций.
    public sealed class SampleApiKeys(SampleSettings settings) : IApiKeyValidator
    {
        public ValueTask<ClaimsPrincipal?> ValidateAsync(string key, CancellationToken token)
        {
            // Пустая настройка не даёт доступа. Сравниваем хеши одинаковой длины через
            // FixedTimeEquals, чтобы сравнение не завершалось на первом несовпавшем символе.
            var valid = !string.IsNullOrWhiteSpace(settings.ApiKey) && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey)));
            // null означает отказ. Успешный principal содержит стабильный ID и имя интеграции.
            // Непустой authenticationType нужен, чтобы ClaimsIdentity считалась аутентифицированной.
            return ValueTask.FromResult(valid ? new ClaimsPrincipal(new ClaimsIdentity(
                [new(ClaimTypes.NameIdentifier, "sample-integration"), new(ClaimTypes.Name, "sample-integration")], AuthSchemes.ApiKey)) : null);
        }
    }

    // Явная начальная настройка администратора, которую Точка входа выше ждёт до старта сервера.
    // Публичная регистрация не принимает роли: повышенные права назначает серверный код.
    public static class AdminSeed
    {
        public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            var settings = services.GetRequiredService<SampleSettings>();
            // Без настройки пароля не создаём аккаунт с предсказуемыми учётными данными.
            if (string.IsNullOrWhiteSpace(settings.AdminPassword)) return;
            // IUserAccounts выполняет регистрацию/проверку пароля, IUserStore хранит аккаунты и роли.
            var users = services.GetRequiredService<IUserAccounts>();
            var store = services.GetRequiredService<IUserStore>();
            var existing = await store.FindByNameAsync("admin", cancellationToken);
            if (existing == null)
            {
                // Регистрация хеширует пароль и создаёт обычного User; затем выдаём Admin.
                var user = await users.RegisterAsync(new RegisterRequest { Username = "admin", Password = settings.AdminPassword }, cancellationToken);
                await store.SetRolesAsync(user.Id, ["User", "Admin"], cancellationToken);
            }
            else
            {
                // Совпадение имени не даёт права повысить чужой аккаунт: сначала проверяем пароль.
                // Неверный пароль прерывает запуск, существующий пароль молча не перезаписывается.
                await users.AuthenticateAsync(new LoginRequest { Username = "admin", Password = settings.AdminPassword }, cancellationToken);
                // Сохраняем остальные роли и не переназначаем их при каждом перезапуске.
                // Изменение ролей отзывает refresh-сессии; уже выданный access JWT живёт до expiry.
                if (!existing.Roles.Contains("Admin", StringComparer.Ordinal))
                    await store.SetRolesAsync(existing.Id, existing.Roles.Concat(["Admin"]).Distinct().ToArray(), cancellationToken);
            }
        }
    }

    // 6. Типизированный WebSocket-чат
    // Входящее JSON-сообщение: {"name":"Alice","text":"Hello"}.
    // Trexon.Web десериализует и проверяет DataAnnotations до OnMessageAsync.
    // При ошибке клиент получает JSON-конверт error и может исправить сообщение в том же соединении.
    public sealed class ChatMessage
    {
        // В публичном чате имя — неподтверждённая подпись, а не доказательство личности.
        [Required, StringLength(40)]
        public string Name { get; set; } = "Guest";

        [Required, StringLength(2000)]
        public string Text { get; set; } = "";
    }

    // Исходящий JSON: type различает служебное событие connected и сообщение message.
    // At — серверное время UTC; клиенту не нужно присылать собственную метку времени.
    public sealed record ChatEvent(string Type, string Name, string Text, DateTimeOffset At);

    // Socket задаёт адрес подключения; room читается из пути, Tag/Summary описывают его в /docs.
    // Generic-параметры задают входную и выходную модели: ручной receive-loop не требуется.
    // TimeProvider приходит из DI; класс допускает наследование для защищённого варианта ниже.
    [Socket("/ws/chat/{room}"), Tag("Realtime"), Summary("Public chat with isolated rooms", Description = "JSON ChatMessage → ChatEvent. Names are display labels, not authentication credentials.")]
    public class ChatSocket(TimeProvider time) : SocketHandler<ChatMessage, ChatEvent>
    {
        // Вызывается один раз после успешного WebSocket upgrade.
        public override async ValueTask OnConnectedAsync(SocketConnection<ChatEvent> connection, CancellationToken token)
        {
            var room = connection.Route("room")!;
            // Проверяем имя на сервере: браузерные ограничения можно обойти другим клиентом.
            if (room.Length is < 1 or > 32 || room.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            {
                await connection.CloseAsync("Use a room name with 1-32 letters, digits, hyphens or underscores", token);
                return;
            }
            // Join меняет локальное членство в группе, поэтому не требует async.
            // Соединения и комнаты существуют в памяти процесса; история не сохраняется.
            connection.Join(room);
            // SendAsync отправляет событие только этому клиенту, не всем участникам комнаты.
            await connection.SendAsync(new("connected", "System", $"Connected to {room}", time.GetUtcNow()), token);
        }

        // Вызывается для каждого валидного сообщения; внутри одного соединения вызовы последовательны.
        // Имя вошедшего аккаунта берём из identity, а публичную подпись — из входной модели.
        // BroadcastAsync отправляет событие всей комнате, включая отправителя, и принимает токен отмены.
        public override ValueTask OnMessageAsync(ChatMessage message, SocketConnection<ChatEvent> connection, CancellationToken token) =>
            connection.BroadcastAsync(new("message", connection.User.Identity?.Name ?? message.Name.Trim(), message.Text.Trim(), time.GetUtcNow()), connection.Route("room"), token);

        // Чтение фрагментов, ограничения сообщений, отправку JSON и очистку членства при
        // отключении выполняет библиотека: для этого примера OnDisconnectedAsync не нужен.
    }

    // Переиспользуем весь обработчик, добавляя обязательную аутентификацию ДО upgrade.
    // У public/private разные реестры: одинаковое имя комнаты не смешивает их участников.
    // Браузер входит через cookie по HTTPS и передаёт её при WSS-подключении автоматически.
    // Native-клиент может задать Authorization: Bearer или X-API-Key при подключении.
    [Socket("/ws/private/{room}"), Authorize, Summary("Authenticated chat", Description = "Identity is checked before upgrade. Use username/password, Bearer tokens or API keys in /docs/ws; custom browser clients can use HTTPS cookies.")]
    public sealed class PrivateChatSocket(TimeProvider time) : ChatSocket(time);

    // 7. Встроенный браузерный клиент: строка отдаётся GET /chat без отдельных файлов.
    // Raw string сохраняет HTML/JS как есть; ниже подробно описан каждый обработчик.
    public static class SamplePages
    {
        public const string Chat = """
        <!doctype html>
        <html lang="ru">
        <head>
            <meta charset="utf-8">
            <!-- Страница адаптируется к ширине экрана; внешние стили и JS-библиотеки не нужны. -->
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Trexon.Web — WebSocket чат</title>
            <style>
                /* Общая палитра и типографика самостоятельной страницы примера. */
                :root {
                    color-scheme: light dark;
                    font: 16px/1.5 system-ui, sans-serif;
                    background: #10161e;
                    color: #e3edf5;
                }
                body { max-width: 860px; margin: 3rem auto; padding: 0 1rem; }
                h1 { margin-bottom: .25rem; }
                a { color: #7bc5ff; }
                p { color: #b5c4d3; }

                /* Поля переносятся на следующую строку, когда им не хватает ширины. */
                form, .controls { display: flex; gap: .75rem; flex-wrap: wrap; align-items: end; }
                label { display: flex; flex-direction: column; gap: .3rem; flex: 1; }
                input, select, button {
                    font: inherit;
                    padding: .65rem .8rem;
                    border: 1px solid #40546a;
                    border-radius: 7px;
                    background: #1a2736;
                    color: inherit;
                }
                button { background: #216a8c; cursor: pointer; }
                button:disabled { opacity: .45; cursor: default; }
                input { min-width: 100px; }

                /* Прокручиваемая лента; длинный пользовательский текст не ломает ширину страницы. */
                #messages {
                    height: 320px;
                    overflow: auto;
                    border: 1px solid #40546a;
                    border-radius: 8px;
                    padding: 1rem;
                    list-style: none;
                    background: #131e2b;
                }
                #messages li { margin-bottom: .7rem; overflow-wrap: anywhere; }
                #messages .system { color: #9cb2c7; }
                #messages .error, #error { color: #ffb4ae; }
                #send-form { margin-top: 1rem; }
                #message { flex: 1; }
                details { padding: 1rem; border: 1px solid #40546a; border-radius: 8px; margin: 1.5rem 0; }
                summary { cursor: pointer; margin-bottom: .75rem; }
                #state { font-weight: 600; }
                small { color: #9cb2c7; }
                footer { margin-top: 2rem; font-size: .9rem; }
                #login-form label { flex-basis: 180px; }
            </style>
        </head>
        <body>
            <header>
                <a href="/docs">← Документация API</a>
                <h1>WebSocket чат</h1>
                <p>Типизированные сообщения, комнаты и валидация — через один обработчик Trexon.Web.</p>
            </header>

            <!-- Для public вход не нужен. Для private используем существующий аккаунт и cookie-сессию. -->
            <details>
                <summary>Cookie-вход для защищённого чата</summary>
                <p>Создайте аккаунт через <code>/docs</code>. Для входа и защищённого браузерного чата запустите пример по HTTPS.</p>
                <form id="login-form">
                    <label>Имя аккаунта<input id="username" required autocomplete="username"></label>
                    <label>Пароль<input id="password" type="password" required autocomplete="current-password"></label>
                    <button>Войти</button>
                </form>
                <p id="account" role="status">Вход не выполнен</p>
            </details>

            <!-- Имя — публичная подпись. В защищённом чате сервер заменит её именем аккаунта.
                 pattern/maxlength помогают пользователю; сервер повторяет проверку независимо от HTML. -->
            <form id="connect-form">
                <label>Ваше имя<input id="name" value="Гость" maxlength="40" required></label>
                <label>Комната<input id="room" value="general" pattern="[a-zA-Z0-9_-]{1,32}" maxlength="32" required></label>
                <label>Режим
                    <select id="mode">
                        <option value="chat">Публичный</option>
                        <option value="private">С авторизацией</option>
                    </select>
                </label>
                <button id="connect">Подключиться</button>
                <!-- type=button не отправляет форму подключения повторно. -->
                <button id="disconnect" type="button" disabled>Отключиться</button>
            </form>

            <!-- role и aria-live позволяют средствам доступности сообщать о смене состояния. -->
            <p>Состояние: <span id="state" role="status">отключён</span></p>
            <p id="error" role="alert"></p>
            <ul id="messages" aria-live="polite" aria-label="Сообщения чата"></ul>
            <!-- До открытия WebSocket отправка отключена; controls() меняет доступность элементов. -->
            <form id="send-form">
                <label>Сообщение<input id="message" maxlength="2000" required disabled autocomplete="off"></label>
                <button id="send" disabled>Отправить</button>
            </form>
            <footer>
                <small>Откройте вторую вкладку в той же комнате для рассылки. Другая комната изолирована. История не хранится. Имена в публичном чате — подписи, а не подтверждённые личности.</small>
            </footer>

            <script>
                // Один активный сокет на вкладку. Вспомогательная функция сокращает поиск DOM-элементов.
                const el = id => document.getElementById(id);
                let socket = null;

                // Добавляем обычный текст через textContent: пользовательский HTML не исполняется.
                // kind выбирает оформление обычного сообщения, системного события или ошибки.
                function append(text, kind = '') {
                    const item = document.createElement('li');
                    item.textContent = text;
                    item.className = kind;
                    el('messages').append(item);
                    // Ограничиваем число элементов в DOM. Это локальная лента, сервер историю не хранит.
                    while (el('messages').children.length > 200) {
                        el('messages').firstElementChild.remove();
                    }
                    el('messages').scrollTop = el('messages').scrollHeight;
                }

                // На время подключения блокируем повторный submit и смену комнаты/режима.
                // Отправлять сообщения разрешаем только после события open.
                function controls(connected, busy = false) {
                    el('connect').disabled = connected || busy;
                    el('disconnect').disabled = !connected;
                    el('message').disabled = !connected;
                    el('send').disabled = !connected;
                    el('room').disabled = connected || busy;
                    el('mode').disabled = connected || busy;
                }

                el('connect-form').addEventListener('submit', event => {
                    event.preventDefault();
                    // readyState 0/1 — CONNECTING/OPEN: второе соединение пока не создаём.
                    if (socket && socket.readyState < 2) return;
                    el('error').textContent = '';
                    if (el('mode').value === 'private' && location.protocol !== 'https:') {
                        el('error').textContent = 'Для cookie-входа в браузере нужен HTTPS.';
                        return;
                    }

                    controls(false, true);
                    el('state').textContent = 'подключение…';
                    // На HTTPS-странице используем WSS; хост и порт берём от текущей страницы.
                    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
                    // Браузерный WebSocket не позволяет задать произвольный Authorization header.
                    // Для private браузер сам приложит подходящую HTTP-only cookie после входа.
                    // Токены в URL не передаём; encodeURIComponent кодирует сегмент имени комнаты.
                    socket = new WebSocket(`${scheme}://${location.host}/ws/${el('mode').value}/${encodeURIComponent(el('room').value)}`);

                    socket.onopen = () => {
                        controls(true);
                        el('state').textContent = 'подключён';
                        el('message').focus();
                    };

                    socket.onmessage = event => {
                        try {
                            // Сервер отправляет либо типизированный ChatEvent, либо конверт error.
                            const message = JSON.parse(event.data);
                            if (message.error) {
                                // Ошибки валидации содержат массив сообщений для каждого поля.
                                const fields = Object.entries(message.error.fields || {})
                                    .map(([name, errors]) => `${name}: ${errors.join(', ')}`)
                                    .join('; ');
                                append(`${message.error.code}: ${message.error.message}${fields ? ' — ' + fields : ''}`, 'error');
                                return;
                            }
                            append(`${message.name}: ${message.text}`, message.type === 'connected' ? 'system' : '');
                        } catch {
                            // Диагностический fallback, если сервер прислал текст другого формата.
                            append(event.data);
                        }
                    };

                    // Если handshake отклонён (например, 401), браузер не раскрывает здесь HTTP-тело.
                    socket.onerror = () => {
                        el('error').textContent = 'Не удалось открыть соединение. Для защищённого режима проверьте cookie-вход и HTTPS.';
                    };
                    socket.onclose = event => {
                        controls(false);
                        el('state').textContent = 'отключён';
                        append(`Соединение закрыто (${event.code})${event.reason ? ': ' + event.reason : ''}`, 'system');
                        socket = null;
                        // Автоматического reconnect нет: новое подключение делает пользователь.
                    };
                });

                // 1000 — нормальное завершение WebSocket-сессии.
                el('disconnect').addEventListener('click', () => socket?.close(1000, 'Finished'));

                el('send-form').addEventListener('submit', event => {
                    event.preventDefault();
                    if (socket?.readyState !== WebSocket.OPEN) return;
                    // Имена JSON-полей соответствуют ChatMessage. Валидацию выполнит сервер.
                    socket.send(JSON.stringify({ name: el('name').value, text: el('message').value }));
                    el('message').value = '';
                    // Здесь сообщение в ленту не добавляем: отправитель тоже получит broadcast.
                });

                el('login-form').addEventListener('submit', async event => {
                    event.preventDefault();
                    // Cookie-аутентификация примера использует Secure-cookie, поэтому нужен HTTPS.
                    if (location.protocol !== 'https:') {
                        el('account').textContent = 'Cookie-вход требует HTTPS.';
                        return;
                    }
                    try {
                        // Сначала получаем CSRF request token и соответствующую cookie.
                        // CSRF-токен защищает изменяющий HTTP-запрос; он не является access JWT.
                        const csrfResponse = await fetch('/auth/cookie/csrf', { credentials: 'same-origin' });
                        const csrf = await csrfResponse.json();
                        if (!csrfResponse.ok) throw new Error(csrf.detail || csrf.title);

                        // credentials разрешает отправлять/принимать cookie на том же origin.
                        // Пароль передаём JSON-телом, CSRF request token — специальным заголовком.
                        const response = await fetch('/auth/cookie/login', {
                            method: 'POST',
                            credentials: 'same-origin',
                            headers: {
                                'Content-Type': 'application/json',
                                'X-CSRF-Token': csrf.requestToken
                            },
                            body: JSON.stringify({ username: el('username').value, password: el('password').value })
                        });
                        const result = await response.json();
                        if (!response.ok) throw new Error(result.detail || result.title);
                        el('account').textContent = `Выполнен вход: ${result.username}`;
                        el('password').value = '';
                        // HTTP-only cookie читает браузер, JavaScript не извлекает её значение.
                        // Теперь можно открыть private-сокет. Для последующих изменяющих HTTP-запросов
                        // нужен новый CSRF-токен, связанный уже с вошедшим пользователем (см. README).
                    } catch (error) {
                        el('account').textContent = error.message;
                    }
                });

                // При уходе со страницы пытаемся штатно закрыть соединение; очистка на сервере автоматическая.
                window.addEventListener('pagehide', () => socket?.close(1000, 'Page closed'));
            </script>
        </body>
        </html>
        """;
    }
}
