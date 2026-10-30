using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Trexon.Web.Testing;
using TrexonApi;
using Xunit;

public sealed class NotesTests
{
    [Fact]
    public async Task Creates_and_reads_a_note()
    {
        // An isolated database per test, real routes/validation/DI, no TCP listener.
        var directory = Path.Combine(Path.GetTempPath(), "trexon-notes-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var api = await TrexonTest.CreateAsync(app =>
                app.AddModule(new NotesModule(new AppSettings { DataDirectory = directory })));
            var created = await api.Client.PostAsJsonAsync("/notes", new { title = "First note", text = "Hello" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var note = await created.Content.ReadFromJsonAsync<Note>();
            var saved = await api.Client.GetFromJsonAsync<Note>($"/notes/{note!.Id}");
            Assert.Equal("First note", saved!.Title);
            var invalid = await api.Client.PostAsJsonAsync("/notes", new { title = "", text = "" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally
        {
            // SQLite pooling retains native file handles after closing DbContexts.
            // Release only this database's pool before deleting its files on Windows.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(Path.Combine(directory, "notes.db"))
            }.ConnectionString);
            SqliteConnection.ClearPool(connection);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
