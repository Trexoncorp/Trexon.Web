using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trexon.Web.Security;

namespace Trexon.Web.Users;

/// <summary>Adds local registration, JWT login, refresh rotation, logout, and optional cookie sessions.</summary>
public sealed class UsersModule : ITrexonModule
{
    private readonly string? _database;
    private readonly Action<UsersOptions>? _configure;
    /// <summary>Uses SQLite when a path is supplied; otherwise register an IUserStore before building.</summary>
    public UsersModule(string? databasePath = null, Action<UsersOptions>? configure = null)
    { _database = databasePath; _configure = configure; }
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        if (!app.HasLocalJwt) throw new InvalidOperationException("Call UseJwt(JwtSettings) before UseUsers; local accounts need a token issuer.");
        var options = new UsersOptions(); _configure?.Invoke(options);
        if (!options.Prefix.StartsWith('/') || options.Prefix.Contains('{') || options.Prefix == "/") throw new ArgumentException("Users prefix must be a non-root absolute path without parameters.");
        if (options.RefreshLifetime <= TimeSpan.Zero || options.RefreshLifetime > TimeSpan.FromDays(90)) throw new ArgumentException("Refresh lifetime must be positive and at most 90 days.");
        if (options.RegistrationRoles.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Registration roles cannot be empty.");
        app.DocsLoginPath = options.Prefix.TrimEnd('/') + "/login";
        app.Services.AddSingleton(options);
        if (_database != null) app.Services.TryAddSingleton<IUserStore>(_ => new SqliteUserStore(_database));
        app.Services.TryAddSingleton<IPasswordHasher<StoredUser>, PasswordHasher<StoredUser>>();
        app.Services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210_000);
        app.Services.AddSingleton<IUserAccounts, UserAccounts>();
        app.AddRoutes<AccountRoutes>();
        app.Services.Configure<MvcOptions>(mvc => mvc.Conventions.Add(new AccountPrefixConvention(options.Prefix, app)));
    }
    /// <inheritdoc />
    public async ValueTask InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await services.GetRequiredService<IUserStore>().InitializeAsync(cancellationToken);
        _ = services.GetRequiredService<IUserAccounts>();
    }
}
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ValidateCookieCsrfAttribute : Attribute;

internal sealed class AccountPrefixConvention(string prefix, TrexonApp app) : Microsoft.AspNetCore.Mvc.ApplicationModels.IApplicationModelConvention
{
    public void Apply(Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel model)
    {
        foreach (var controller in model.Controllers.Where(c => c.ControllerType == typeof(AccountRoutes)))
        {
            foreach (var selector in controller.Selectors)
                if (selector.AttributeRouteModel != null) selector.AttributeRouteModel.Template = prefix.Trim('/');
            if (!app.SecuritySchemes.Any(s => s.Id == AuthSchemes.Cookie))
                for (var i = controller.Actions.Count - 1; i >= 0; i--)
                    if (controller.Actions[i].ActionName.StartsWith("Cookie", StringComparison.Ordinal)) controller.Actions.RemoveAt(i);
        }
    }
}

/// <summary>Built-in account HTTP endpoints, connected by UsersModule.</summary>
[Routes("/auth"), Tag("Accounts")]
public sealed class AccountRoutes(IUserAccounts accounts, IHttpContextAccessor accessor, IUserStore store)
{
    private HttpContext Context => accessor.HttpContext!;
    /// <summary>Registers a local account without accepting client-supplied roles.</summary>
    [Post("register"), AllowAnonymous, Summary("Register an account"), Response(201, typeof(UserInfo)), Response(409)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken token) => new ObjectResult(await accounts.RegisterAsync(request, token)) { StatusCode = 201 };
    /// <summary>Authenticates a local account and creates a token pair.</summary>
    [Post("login"), AllowAnonymous, Summary("Sign in and obtain access/refresh tokens"), Response(200, typeof(TokenPair)), Response(401)]
    public Task<TokenPair> Login(LoginRequest request, CancellationToken token) => accounts.LoginAsync(request, token);
    /// <summary>Atomically replaces a refresh token.</summary>
    [Post("refresh"), AllowAnonymous, Summary("Rotate a refresh token"), Response(401)]
    public Task<TokenPair> Refresh(RefreshRequest request, CancellationToken token) => accounts.RefreshAsync(request.RefreshToken, token);
    /// <summary>Revokes a refresh family; does not invalidate already-issued access tokens.</summary>
    [Post("logout"), AllowAnonymous, Summary("Revoke a refresh-token family"), Response(204)]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken token)
    { await accounts.LogoutAsync(request.RefreshToken, token); return new NoContentResult(); }
    /// <summary>Returns the authenticated local account.</summary>
    [Get("me"), Authorize, Summary("Get the current local account"), Response(200, typeof(UserInfo))]
    public async Task<UserInfo> Me(CancellationToken token)
    {
        var id = Context.User.FindFirstValue("sub") ?? Context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = id == null ? null : await store.FindByIdAsync(id, token);
        return user == null ? throw new HttpError(404, "Local account not found.") : new(user.Id, user.Username, user.Roles);
    }
    /// <summary>Signs in with an HTTP-only cookie when CookieModule is registered.</summary>
    [Post("cookie/login"), AllowAnonymous, ValidateCookieCsrf, Summary("Start a cookie session (HTTPS)"), Response(200, typeof(UserInfo))]
    public async Task<UserInfo> CookieLogin(LoginRequest request, CancellationToken token)
    {
        await RequireCookies();
        var user = await accounts.AuthenticateAsync(request, token);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.Username) };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        await Context.SignInAsync(AuthSchemes.Cookie, new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Cookie)));
        return user;
    }
    /// <summary>Signs out a cookie session. Only POST is accepted; validate Origin on cookie mutations.</summary>
    [Post("cookie/logout"), AllowAnonymous, ValidateCookieCsrf, Summary("End a cookie session"), Response(204)]
    public async Task<IActionResult> CookieLogout()
    { await RequireCookies(); await Context.SignOutAsync(AuthSchemes.Cookie); return new NoContentResult(); }
    /// <summary>Creates an antiforgery cookie and returns a request token for X-CSRF-Token.</summary>
    [Get("cookie/csrf"), AllowAnonymous, Summary("Get a CSRF token for cookie authentication")]
    public async Task<object> CookieCsrf()
    {
        await RequireCookies();
        var tokens = Context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(Context);
        return new { requestToken = tokens.RequestToken, headerName = "X-CSRF-Token" };
    }
    private async Task RequireCookies()
    {
        var provider = Context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        if (await provider.GetSchemeAsync(AuthSchemes.Cookie) == null) throw new HttpError(404, "Cookie authentication is not enabled.");
        if (!Context.Request.IsHttps) throw new HttpError(400, "Cookie sessions require HTTPS.");
    }
}

/// <summary>Convenience methods for local user modules.</summary>
public static class UserModuleExtensions
{
    /// <summary>Adds SQLite-backed accounts; call UseJwt first.</summary>
    public static TrexonApp UseUsers(this TrexonApp app, string databasePath, Action<UsersOptions>? configure = null) => app.AddModule(new UsersModule(databasePath, configure));
    /// <summary>Adds accounts backed by an explicitly registered IUserStore.</summary>
    public static TrexonApp UseUsers(this TrexonApp app, Action<UsersOptions>? configure = null) => app.AddModule(new UsersModule(configure: configure));
}
