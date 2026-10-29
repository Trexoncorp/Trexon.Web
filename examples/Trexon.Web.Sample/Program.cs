using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Trexon.Configuration;
using Trexon.Logging;
using Trexon.Web;
using Trexon.Web.Modules;
using Trexon.Web.Sample;
using Trexon.Web.Security;
using Trexon.Web.Users;
using Trexon.Web.Sockets;

// Точка входа: dotnet run --project examples/Trexon.Web.Sample.
// Trexon.Configuration связывает переменные окружения с [Env] в SampleSettings.
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
    // HTML встроен в сборку через csproj: текущий рабочий каталог не влияет на его поиск.
    web.MapGet("/chat", () => Results.Stream(typeof(ChatSocket).Assembly.GetManifestResourceStream("Trexon.Web.Sample.Chat.html")!, "text/html; charset=utf-8"))
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
