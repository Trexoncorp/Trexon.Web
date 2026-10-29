using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Trexon.Data;

namespace Trexon.Web.Sample;

// Модуль объединяет настройки DI, маршруты, middleware и асинхронную подготовку данных.
// Чтобы подключить весь функционал задач, в Program.cs достаточно AddModule<TaskModule>().
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

// Program.cs сопоставляет это исключение с HTTP 404. Одинаковый результат для
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
