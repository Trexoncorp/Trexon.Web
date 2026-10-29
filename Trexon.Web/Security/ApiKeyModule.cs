using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Trexon.Web.Internal;

namespace Trexon.Web.Security;

/// <summary>Resolves an API key to an identity; return null for an invalid or revoked key.</summary>
public interface IApiKeyValidator
{
    /// <summary>Checks the key against the application's trusted store.</summary>
    ValueTask<ClaimsPrincipal?> ValidateAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Names of built-in authentication schemes.</summary>
public static class AuthSchemes
{
    /// <summary>Local or external JWT bearer authentication.</summary>
    public const string Bearer = "Bearer";
    /// <summary>API key authentication.</summary>
    public const string ApiKey = "ApiKey";
    /// <summary>Cookie authentication.</summary>
    public const string Cookie = "Cookies";
}
internal sealed class ApiKeyOptions : AuthenticationSchemeOptions
{
    public string Header { get; set; } = "X-API-Key";
}
internal sealed class ApiKeyHandler(IOptionsMonitor<ApiKeyOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IApiKeyValidator validator) : AuthenticationHandler<ApiKeyOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Options.Header, out var values)) return AuthenticateResult.NoResult();
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 4096) return AuthenticateResult.Fail("Invalid API key.");
        var principal = await validator.ValidateAsync(values[0]!, Context.RequestAborted);
        return principal?.Identity?.IsAuthenticated == true
            ? AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name))
            : AuthenticateResult.Fail("Invalid API key.");
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    { Response.Headers.WWWAuthenticate = AuthSchemes.ApiKey; return Problems.WriteAsync(Context, 401, "Unauthorized"); }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => Problems.WriteAsync(Context, 403, "Forbidden");
}

/// <summary>Registers an application API key validator and header-based Swagger authentication.</summary>
public sealed class ApiKeyModule<TValidator>(string header = "X-API-Key") : ITrexonModule where TValidator : class, IApiKeyValidator
{
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("API key header must contain ASCII letters, digits, or hyphens.", nameof(header));
        app.ApiKeyHeader = header;
        app.Services.AddScoped<IApiKeyValidator, TValidator>();
        app.Services.AddAuthentication().AddScheme<ApiKeyOptions, ApiKeyHandler>(AuthSchemes.ApiKey, options => options.Header = header);
        app.AddSecurity(new SecurityDescription(AuthSchemes.ApiKey, SecuritySchemeType.ApiKey, Header: header),
            new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, Name = header, In = ParameterLocation.Header });
    }
}

/// <summary>Registers a secure, HTTP-only cookie with 401/403 API responses.</summary>
public sealed class CookieModule(string cookieName = "Trexon.Session") : ITrexonModule
{
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookieName);
        app.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-Token";
            options.Cookie.Name = cookieName + ".Csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        app.Services.AddAuthentication().AddCookie(AuthSchemes.Cookie, options =>
        {
            options.Cookie.Name = cookieName; options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context => Problems.WriteAsync(context.HttpContext, 401, "Unauthorized");
            options.Events.OnRedirectToAccessDenied = context => Problems.WriteAsync(context.HttpContext, 403, "Forbidden");
        });
        app.AddSecurity(new SecurityDescription(AuthSchemes.Cookie, SecuritySchemeType.ApiKey, Header: cookieName, Location: ParameterLocation.Cookie),
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = cookieName,
                Description = "Sign in over HTTPS with X-CSRF-Token. The browser sends its HTTP-only cookie automatically; Authorize cannot set it manually."
            });
    }
    /// <inheritdoc />
    public void Use(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if ((context.User.Identities.Any(i => i.AuthenticationType == AuthSchemes.Cookie) ||
                 context.GetEndpoint()?.Metadata.GetMetadata<Trexon.Web.Users.ValidateCookieCsrfAttribute>() != null) &&
                !context.WebSockets.IsWebSocketRequest &&
                !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { await Problems.WriteAsync(context, 400, "Invalid CSRF token"); return; }
            }
            await next(context);
        });
    }
}
