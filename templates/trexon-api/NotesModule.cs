using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Trexon.Data;
using Trexon.Logging;
using Trexon.Web;
using Trexon.Web.Hosting;
using Trexon.Web.Modules;
using DataSource = Trexon.Data.Data;

namespace TrexonApi;

// Reuse this module in production and in-process tests: one registration path.
public sealed class NotesModule(AppSettings settings) : ITrexonModule
{
    public void Configure(TrexonApp app)
    {
        if (!IPAddress.TryParse(settings.Host, out var address))
            throw new ArgumentException("APP_HOST must be a valid listen IP address.");
        app.Builder.WebHost.UseUrls(new UriBuilder("http", address.ToString(), settings.Port).Uri.AbsoluteUri);
        app.Services.AddSingleton(settings);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(Path.Combine(settings.DataDirectory, "notes.db"))
        };
        app.Services.AddTrexonData(config => config
            .UseSqlite(connection.ConnectionString)
            .Entity<Note>(entity =>
            {
                entity.ToTable("Notes");
                entity.HasKey(note => note.Id);
                entity.Property(note => note.Title).HasMaxLength(120);
                entity.Property(note => note.Text).HasMaxLength(2000);
            }));
        app.AddRoutes<NotesRoutes>().AddStartup<SchemaStartup>().UseHealthChecks();
        app.Services.AddHealthChecks().AddCheck<NotesHealthCheck>("notes-sqlite");
        app.Map(web => web.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription());
    }
}

// DI resolves DataSource. Initialization finishes and the scope is disposed before listening.
public sealed class SchemaStartup(DataSource data, AppSettings settings) : ITrexonStartup
{
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(settings.DataDirectory);
        var schema = await data.UpdateAsync(cancellationToken);
        Log.Info("Schema ready: Changed={Changed}, Operations={Operations}", schema.Changed, schema.OperationCount);
    }
}
