using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OutputCaching;
using Trexon.Web.Security;
using Trexon.Web.Users;

namespace Trexon.Web.Sample;

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

    // UseOutputCache в Program.cs включает механизм, а атрибут задаёт срок для маршрута.
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

    // Именованное правило manage-tasks зарегистрировано в Program.cs.
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

// Явная начальная настройка администратора, которую Program.cs ждёт до старта сервера.
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
