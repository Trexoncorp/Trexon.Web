using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Trexon.Data;
using Trexon.Logging;
using Trexon.Web;

namespace TrexonApi;

[Routes("/notes"), Tag("Notes")]
public sealed class NotesRoutes(DataSession db, IHttpContextAccessor accessor)
{
    [Get, Summary("List notes, newest first"), Response(200, typeof(Note[])), Response(400)]
    public Task<Note[]> List(CancellationToken token, bool? archived = null,
        [Range(0, int.MaxValue)] int skip = 0, [Range(1, 100)] int take = 20)
    {
        var query = db.Table<Note>().AsNoTracking();
        if (archived.HasValue) query = query.Where(note => note.Archived == archived.Value);
        return query.OrderByDescending(note => note.Id).Skip(skip).Take(take).ToArrayAsync(token);
    }

    [Get("{id:int}"), Summary("Read a note"), Response(200, typeof(Note)), Response(404)]
    public async Task<Note> Get(int id, CancellationToken token) =>
        await db.Table<Note>().AsNoTracking().SingleOrDefaultAsync(note => note.Id == id, token)
        ?? throw Missing();

    [Post, Summary("Create a note"), Response(201, typeof(Note)), Response(400)]
    public async Task<IActionResult> Create(SaveNote request, CancellationToken token)
    {
        var note = new Note { Title = request.Title.Trim(), Text = request.Text.Trim(), Archived = request.Archived };
        db.Add(note);
        await db.SaveAsync(token);
        WriteEvent("Created", note.Id);
        return new CreatedResult($"/notes/{note.Id}", note);
    }

    [Put("{id:int}"), Summary("Update or archive a note"), Response(200, typeof(Note)), Response(400), Response(404)]
    public async Task<Note> Update(int id, SaveNote request, CancellationToken token)
    {
        // SaveAsync persists tracked changes to the loaded entity.
        var note = await db.Table<Note>().SingleOrDefaultAsync(note => note.Id == id, token) ?? throw Missing();
        note.Title = request.Title.Trim();
        note.Text = request.Text.Trim();
        note.Archived = request.Archived;
        await db.SaveAsync(token);
        WriteEvent("Updated", id);
        return note;
    }

    [Delete("{id:int}"), Summary("Delete a note"), Response(204), Response(404)]
    public async Task<IActionResult> Delete(int id, CancellationToken token)
    {
        var note = await db.Table<Note>().SingleOrDefaultAsync(note => note.Id == id, token) ?? throw Missing();
        db.Remove(note);
        await db.SaveAsync(token);
        WriteEvent("Deleted", id);
        return new NoContentResult();
    }

    private static HttpError Missing() => new(404, "Note not found.");

    private void WriteEvent(string action, int id)
    {
        // Trexon.Web puts the same ID in its HTTP event.
        using var scope = Log.PushScope("RequestId", accessor.HttpContext!.TraceIdentifier);
        Log.Info("Note {NoteId}: {Action}", id, action);
    }
}
