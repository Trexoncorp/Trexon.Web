using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Trexon.Configuration;
using Trexon.Data;
using Trexon.Logging;
using Trexon.Web;
using Trexon.Web.Modules;
using TrexonApi;
using DataSource = Trexon.Data.Data;

try
{
    // Environment variables override .env; missing values use AppSettings defaults.
    var settings = Settings.Load<AppSettings>(config => config
        .ReadFrom.DotEnv(".env", optional: true)
        .ReadFrom.EnvironmentVariables());

    Directory.CreateDirectory(settings.DataDirectory);
    Directory.CreateDirectory(settings.LogDirectory);
    Log.Configure(config => config
        .MinimumLevel(settings.LogLevel)
        .Enrich.WithProperty("Application", settings.Name)
        .WriteTo.Console(console => console.Timestamp("HH:mm:ss").Colors())
        .WriteTo.File(Path.Combine(settings.LogDirectory, "app.jsonl"), file => file.Json()));

    var app = TrexonApp.Create(args, api => api.Title = settings.Name);
    app.Builder.WebHost.UseUrls($"http://127.0.0.1:{settings.Port}");

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

    app.AddRoutes<NotesRoutes>().UseHealthChecks();
    app.Services.AddHealthChecks().AddCheck<NotesHealthCheck>("notes-sqlite");
    app.Map(web => web.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription());

    await using var web = await app.BuildAsync();
    // Finish schema initialization before accepting HTTP requests.
    var schema = await web.Services.GetRequiredService<DataSource>().UpdateAsync();
    Log.Info("Schema ready: Changed={Changed}, Operations={Operations}", schema.Changed, schema.OperationCount);
    Log.Info("{ApplicationName}: http://127.0.0.1:{Port}/docs", settings.Name, settings.Port);
    await web.RunAsync();
}
catch (SettingsException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}
catch (Exception error)
{
    Log.Error(error, "Application failed");
    Environment.ExitCode = 1;
}
finally
{
    Log.Shutdown();
}
