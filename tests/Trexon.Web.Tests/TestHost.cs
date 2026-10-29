using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Trexon.Web.Security;
using Trexon.Web.Users;

namespace Trexon.Web.Tests;

internal sealed class TestHost(WebApplication app, HttpClient client, string directory) : IAsyncDisposable
{
    internal WebApplication App { get; } = app;
    internal HttpClient Client { get; } = client;
    internal static readonly JwtSettings Jwt = new("trexon-tests", "trexon-tests", "test-only-key-at-least-32-bytes-abcdef0123456789");
    internal static async Task<TestHost> Start(Action<TrexonApp>? configure = null, bool users = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "trexon-web-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = TrexonApp.Create(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Builder.Logging.ClearProviders();
        builder.Builder.WebHost.UseTestServer();
        if (users) builder.UseJwt(Jwt).UseUsers(Path.Combine(directory, "users.db"));
        configure?.Invoke(builder);
        WebApplication? app = null;
        try
        {
            app = await builder.BuildAsync(TestContext.Current.CancellationToken); await app.StartAsync(TestContext.Current.CancellationToken);
            var client = app.GetTestClient(); client.BaseAddress = new Uri("https://localhost");
            return new(app, client, directory);
        }
        catch { if (app != null) await app.DisposeAsync(); Directory.Delete(directory, true); throw; }
    }
    internal async Task<JsonObject> Json(string path) => (await Client.GetFromJsonAsync<JsonObject>(path))!;
    internal async Task<TokenPair> RegisterLogin(string username = "tester")
    {
        var register = await Client.PostAsJsonAsync("/auth/register", new { username, password = "long-test-password-123" }, cancellationToken: TestContext.Current.CancellationToken);
        register.EnsureSuccessStatusCode();
        var login = await Client.PostAsJsonAsync("/auth/login", new { username, password = "long-test-password-123" }, cancellationToken: TestContext.Current.CancellationToken);
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<TokenPair>(cancellationToken: TestContext.Current.CancellationToken))!;
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose(); await App.StopAsync(); await App.DisposeAsync(); Directory.Delete(directory, true);
    }
}
