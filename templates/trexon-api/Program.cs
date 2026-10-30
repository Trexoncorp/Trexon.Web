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
    app.AddModule(new NotesModule(settings));
    await using var web = await app.BuildAsync();
    Log.Info("{ApplicationName}: http://localhost:{Port}/docs", settings.Name, settings.Port);
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
