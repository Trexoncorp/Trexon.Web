using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Trexon.Web.Sample;

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
