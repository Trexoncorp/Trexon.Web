using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Trexon.Data;

namespace Trexon.Web.Sample;

public sealed class TaskModule : ITrexonModule
{
    public void Configure(TrexonApp app)
    {
        app.Services.AddSingleton(provider => Data.Data.Create(config => config
            .UseSqlite("Data Source=" + Path.Combine(provider.GetRequiredService<SampleSettings>().DataDirectory, "tasks.db"))
            .Entity<TaskItem>(entity => { entity.HasKey(task => task.Id); entity.Property(task => task.Title).HasMaxLength(120); })));
        app.Services.AddScoped<TaskService>();
        app.AddValidator<CreateTask, TaskTitleValidator>();
        app.AddRoutes<TaskRoutes>();
        app.Services.AddHealthChecks().AddCheck<TaskDatabaseHealth>("tasks-sqlite");
    }
    public void Use(WebApplication web) => web.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/tasks")) context.Response.Headers["X-Task-Module"] = "enabled";
        await next(context);
    });
    public async ValueTask InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default) => await services.GetRequiredService<Data.Data>().UpdateAsync(cancellationToken);
}
public sealed class TaskItem
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Completed { get; set; }
}
public sealed class CreateTask
{
    [Required, StringLength(120, MinimumLength = 3)] public string Title { get; set; } = "";
}
public sealed class UpdateTask
{
    [Required, StringLength(120, MinimumLength = 3)] public string Title { get; set; } = "";
    public bool Completed { get; set; }
}
public sealed record TaskView(int Id, string Title, bool Completed);
public sealed class TaskNotFoundException(int id) : Exception($"Task {id} was not found.");
public sealed class TaskTitleValidator : IRequestValidator<CreateTask>
{
    public ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(CreateTask model, CancellationToken token) =>
        ValueTask.FromResult<IEnumerable<ValidationIssue>>(string.IsNullOrWhiteSpace(model.Title) || model.Title.Trim().Length < 3
            ? [new("Title", "Provide at least three non-space characters.")] : []);
}
public sealed class TaskService(Data.Data data)
{
    public async Task<TaskView[]> List(string owner, bool? completed, int skip, int take, CancellationToken token)
    {
        await using var db = data.Open();
        var query = db.Table<TaskItem>().AsNoTracking().Where(task => task.OwnerId == owner);
        if (completed.HasValue) query = query.Where(task => task.Completed == completed.Value);
        return await query.OrderBy(task => task.Id).Skip(skip).Take(take).Select(task => new TaskView(task.Id, task.Title, task.Completed)).ToArrayAsync(token);
    }
    public async Task<TaskView> Get(string owner, int id, CancellationToken token)
    {
        await using var db = data.Open();
        var task = await db.Table<TaskItem>().AsNoTracking().SingleOrDefaultAsync(task => task.Id == id && task.OwnerId == owner, token)
            ?? throw new TaskNotFoundException(id);
        return new(task.Id, task.Title, task.Completed);
    }
    public async Task<TaskView> Create(string owner, CreateTask request, CancellationToken token)
    {
        await using var db = data.Open();
        var task = new TaskItem { OwnerId = owner, Title = request.Title.Trim() };
        db.Add(task); await db.SaveAsync(token);
        return new(task.Id, task.Title, task.Completed);
    }
    public async Task<TaskView> Update(string owner, int id, UpdateTask request, CancellationToken token)
    {
        await using var db = data.Open();
        var task = await db.Table<TaskItem>().SingleOrDefaultAsync(task => task.Id == id && task.OwnerId == owner, token) ?? throw new TaskNotFoundException(id);
        task.Title = request.Title.Trim(); task.Completed = request.Completed;
        await db.SaveAsync(token); return new(task.Id, task.Title, task.Completed);
    }
    public async Task Delete(string owner, int id, CancellationToken token)
    {
        await using var db = data.Open();
        var deleted = await db.Table<TaskItem>().Where(task => task.Id == id && task.OwnerId == owner).ExecuteDeleteAsync(token);
        if (deleted == 0) throw new TaskNotFoundException(id);
    }
}
public sealed class TaskDatabaseHealth(Data.Data data) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var db = data.Open();
        return await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
    }
}
