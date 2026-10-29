using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OutputCaching;
using Trexon.Web.Security;
using Trexon.Web.Users;

namespace Trexon.Web.Sample;

[Routes("/demo"), Tag("Feature tour")]
public sealed class DiagnosticsRoutes(IHttpContextAccessor context)
{
    [Get("public"), Summary("An anonymous JSON response")]
    public ValueTask<object> Public() => ValueTask.FromResult<object>(new { service = "Trexon.Web", docs = "/docs" });

    [Get("headers"), Summary("Bind a value from a request header")]
    public ValueTask<object> Headers([Microsoft.AspNetCore.Mvc.FromHeader(Name = "X-Client")] string client = "unknown") => ValueTask.FromResult<object>(new { client });

    [Get("cached"), OutputCache(Duration = 30), Summary("A public response cached for 30 seconds")]
    public ValueTask<object> Cached() => ValueTask.FromResult<object>(new { generatedAt = DateTimeOffset.UtcNow, value = Guid.NewGuid() });

    [Get("whoami"), Authorize, Summary("Inspect the authenticated identity")]
    public ValueTask<object> WhoAmI() => ValueTask.FromResult<object>(new
    {
        name = context.HttpContext!.User.Identity!.Name,
        roles = context.HttpContext.User.FindAll("role").Concat(context.HttpContext.User.FindAll(ClaimTypes.Role)).Select(c => c.Value)
    });

    [Get("admin"), Authorize(Roles = "Admin"), Summary("Requires the Admin role")]
    public ValueTask<object> Admin() => ValueTask.FromResult<object>(new { message = "Admin access granted" });

    [Get("policy"), Authorize(Policy = "manage-tasks"), Summary("Requires the manage-tasks policy")]
    public ValueTask<object> Policy() => ValueTask.FromResult<object>(new { message = "Policy satisfied" });

    [Get("integration"), Authorize(AuthenticationSchemes = AuthSchemes.ApiKey), Summary("Requires an API key explicitly")]
    public ValueTask<object> Integration() => ValueTask.FromResult<object>(new { message = "Integration authenticated" });

    [Get("conflict"), Summary("A deliberate, client-safe 409 response"), Response(409)]
    public ValueTask<object> Conflict() => throw new HttpError(409, "This task is already completed.");

    [Get("failure"), Summary("A handled unexpected exception"), Response(500)]
    public ValueTask<object> Failure() => throw new InvalidOperationException("Internal implementation details are logged, not returned.");
}

public sealed class SampleApiKeys(SampleSettings settings) : IApiKeyValidator
{
    public ValueTask<ClaimsPrincipal?> ValidateAsync(string key, CancellationToken token)
    {
        var valid = !string.IsNullOrWhiteSpace(settings.ApiKey) && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey)));
        return ValueTask.FromResult(valid ? new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.NameIdentifier, "sample-integration"), new(ClaimTypes.Name, "sample-integration")], AuthSchemes.ApiKey)) : null);
    }
}
public static class AdminSeed
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var settings = services.GetRequiredService<SampleSettings>();
        if (string.IsNullOrWhiteSpace(settings.AdminPassword)) return;
        var users = services.GetRequiredService<IUserAccounts>();
        var store = services.GetRequiredService<IUserStore>();
        var existing = await store.FindByNameAsync("admin", cancellationToken);
        if (existing == null)
        {
            var user = await users.RegisterAsync(new RegisterRequest { Username = "admin", Password = settings.AdminPassword }, cancellationToken);
            await store.SetRolesAsync(user.Id, ["User", "Admin"], cancellationToken);
        }
        else
        {
            // Never promote an account merely because it happens to have the configured seed name.
            await users.AuthenticateAsync(new LoginRequest { Username = "admin", Password = settings.AdminPassword }, cancellationToken);
            if (!existing.Roles.Contains("Admin", StringComparer.Ordinal))
                await store.SetRolesAsync(existing.Id, existing.Roles.Concat(["Admin"]).Distinct().ToArray(), cancellationToken);
        }
    }
}
