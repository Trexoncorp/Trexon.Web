using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Trexon.Configuration;
using Trexon.Logging;
using Trexon.Web;
using Trexon.Web.Modules;
using Trexon.Web.Sample;
using Trexon.Web.Security;
using Trexon.Web.Users;

var settings = Settings.Load<SampleSettings>();
Directory.CreateDirectory(settings.DataDirectory);
using var logger = Logger.Create(config => config.WriteTo.Console());
// No embedded shared secret: without configuration, a key is generated for this process only.
var signingKey = string.IsNullOrWhiteSpace(settings.JwtKey)
    ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) : settings.JwtKey;

var app = TrexonApp.Create(args, api =>
{
    api.Title = "Trexon.Web — Task service";
    api.Description = "Attribute routes, modules, validation, JWT, cookies, API keys, roles, policies, caching and health checks.";
});
app.Builder.WebHost.UseUrls(settings.Url);
app.Services.AddSingleton(settings);
app.Services.AddAuthorization(options => options.AddPolicy("manage-tasks", policy => policy.RequireRole("Admin")));

app.UseJwt(new JwtSettings("trexon-sample", "trexon-sample", signingKey))
    .UseApiKeys<SampleApiKeys>()
    .UseCookies()
    .UseUsers(Path.Combine(settings.DataDirectory, "accounts.db"))
    .UseCors(settings.FrontendOrigin)
    .UseRateLimit(100)
    .UseOutputCache()
    .UseHealthChecks()
    .UseRequestLogging(request => logger.Info("HTTP {Method} {Path} -> {StatusCode} in {ElapsedMs}ms [{TraceId}]",
        request.Method, request.Path, request.StatusCode, request.ElapsedMilliseconds, request.TraceId))
    .AddModule<TaskModule>()
    .AddRoutes<DiagnosticsRoutes>()
    .Errors(errors => errors.Map<TaskNotFoundException>(404, "Task not found"));

app.Map(web => web.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription());
await using var web = await app.BuildAsync();
// Explicit startup work, completed before accepting requests. No background jobs.
await AdminSeed.InitializeAsync(web.Services);
logger.Info("Docs: {DocsUrl}", settings.Url.TrimEnd('/') + "/docs");
if (string.IsNullOrWhiteSpace(settings.JwtKey)) logger.Warning("JWT key is ephemeral; set TREXON_JWT_KEY to preserve access-token validity across restarts.");
await web.RunAsync();
