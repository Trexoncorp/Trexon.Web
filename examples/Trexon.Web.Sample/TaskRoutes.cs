using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Trexon.Web.Sample;

[Routes("/tasks"), Tag("Tasks"), Authorize]
public sealed class TaskRoutes(TaskService tasks, IHttpContextAccessor accessor)
{
    private string Owner => accessor.HttpContext!.User.FindFirstValue("sub")
        ?? accessor.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new HttpError(401, "An account identity is required.");

    [Get, Summary("List your tasks", Description = "Optional completion filter and bounded pagination. Results are scoped to the authenticated owner.")]
    public Task<TaskView[]> List(CancellationToken token, bool? completed = null, [Range(0, int.MaxValue)] int skip = 0, [Range(1, 100)] int take = 20) =>
        tasks.List(Owner, completed, skip, take, token);

    [Get("{id:int}"), Summary("Get one of your tasks"), Response(404)]
    public Task<TaskView> Get(int id, CancellationToken token) => tasks.Get(Owner, id, token);

    [Post, Summary("Create a task"), Response(201, typeof(TaskView)), Response(400, typeof(ValidationProblemDetails))]
    public async Task<IActionResult> Create(CreateTask request, CancellationToken token)
    {
        var task = await tasks.Create(Owner, request, token);
        return new CreatedResult($"/tasks/{task.Id}", task);
    }

    [Put("{id:int}"), Summary("Update a task"), Response(404)]
    public Task<TaskView> Update(int id, UpdateTask request, CancellationToken token) => tasks.Update(Owner, id, request, token);

    [Delete("{id:int}"), Summary("Delete a task"), Response(204), Response(404)]
    public async Task<IActionResult> Delete(int id, CancellationToken token)
    { await tasks.Delete(Owner, id, token); return new NoContentResult(); }
}
