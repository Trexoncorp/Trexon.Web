using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Trexon.Web.Internal;

namespace Trexon.Web.Security;

/// <summary>Explicit settings for local HMAC JWT issuance and validation.</summary>
public sealed class JwtSettings
{
    /// <summary>Creates settings. The signing key must contain at least 32 UTF-8 bytes.</summary>
    public JwtSettings(string issuer, string audience, string signingKey)
    { Issuer = issuer; Audience = audience; SigningKey = signingKey; }
    /// <summary>The expected token issuer.</summary>
    public string Issuer { get; }
    /// <summary>The expected token audience.</summary>
    public string Audience { get; }
    /// <summary>A secret signing key; load it from configuration or a secret store.</summary>
    public string SigningKey { get; }
    /// <summary>Access-token lifetime, default 15 minutes.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Clock tolerance for expiry, default 30 seconds.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Issuer); ArgumentException.ThrowIfNullOrWhiteSpace(Audience);
        if (string.IsNullOrWhiteSpace(SigningKey) || Encoding.UTF8.GetByteCount(SigningKey) < 32)
            throw new ArgumentException("JWT signing key must contain at least 32 UTF-8 bytes.");
        if (Lifetime <= TimeSpan.Zero || Lifetime > TimeSpan.FromDays(1)) throw new ArgumentException("JWT lifetime must be positive and at most one day.");
        if (ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(5)) throw new ArgumentException("JWT clock skew must be between zero and five minutes.");
    }
}

/// <summary>An issued access token and its expiry.</summary>
public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues locally signed access tokens. Registered by UseJwt(JwtSettings).</summary>
public interface ITokenIssuer
{
    /// <summary>Issues a token for a trusted subject with roles and optional application claims.</summary>
    AccessToken Issue(string subject, string? name = null, IEnumerable<string>? roles = null, IEnumerable<Claim>? claims = null);
}
internal sealed class JwtTokenIssuer(JwtSettings settings, TimeProvider time) : ITokenIssuer
{
    public AccessToken Issue(string subject, string? name = null, IEnumerable<string>? roles = null, IEnumerable<Claim>? claims = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var now = time.GetUtcNow(); var expires = now.Add(settings.Lifetime);
        var reserved = new HashSet<string>(["sub", "name", "role", "iss", "aud", "exp", "nbf", "iat", "jti", ClaimTypes.NameIdentifier, ClaimTypes.Name, ClaimTypes.Role], StringComparer.Ordinal);
        var extra = (claims ?? []).ToArray();
        if (extra.Any(c => reserved.Contains(c.Type))) throw new ArgumentException("Additional claims cannot override token identity, roles, issuer, audience or lifetime.", nameof(claims));
        var values = new List<Claim> { new("sub", subject), new("name", name ?? subject), new("jti", Guid.NewGuid().ToString("N")),
            new("iat", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64) };
        values.AddRange((roles ?? []).Distinct(StringComparer.Ordinal).Select(role => new Claim("role", role)));
        values.AddRange(extra);
        var jwt = new JwtSecurityToken(settings.Issuer, settings.Audience, values, now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}

/// <summary>Registers local JWT authentication and the Swagger Bearer security scheme.</summary>
public sealed class JwtModule(JwtSettings settings) : ITrexonModule
{
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        settings.Validate();
        app.Services.AddSingleton(settings);
        app.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        app.Services.AddAuthentication().AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Issuer,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = settings.ClockSkew,
                NameClaimType = "name",
                RoleClaimType = "role"
            };
        });
        app.HasLocalJwt = true;
        SecurityRegistration.AddBearer(app);
    }
}

/// <summary>Validates tokens issued by an external OpenID Connect authority.</summary>
public sealed class AuthorityJwtModule(string authority, string audience) : ITrexonModule
{
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new ArgumentException("JWT authority must be an HTTPS URL.");
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        app.Services.AddAuthentication().AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        { options.Authority = authority; options.Audience = audience; options.MapInboundClaims = false; options.TokenValidationParameters.NameClaimType = "name"; options.TokenValidationParameters.RoleClaimType = "role"; });
        SecurityRegistration.AddBearer(app);
    }
}

internal static class SecurityRegistration
{
    internal static void AddBearer(TrexonApp app) => app.AddSecurity(new SecurityDescription("Bearer", SecuritySchemeType.Http, "bearer"),
        new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "Paste an access token without the Bearer prefix." });
}

/// <summary>Convenience methods for authentication modules.</summary>
public static class SecurityExtensions
{
    /// <summary>Configures local JWT authentication, issuance, and Swagger Authorize together.</summary>
    public static TrexonApp UseJwt(this TrexonApp app, JwtSettings settings) => app.AddModule(new JwtModule(settings));
    /// <summary>Configures external-authority JWT validation and Swagger Authorize.</summary>
    public static TrexonApp UseJwtAuthority(this TrexonApp app, string authority, string audience) => app.AddModule(new AuthorityJwtModule(authority, audience));
    /// <summary>Connects a user-defined API key validator and Swagger Authorize.</summary>
    public static TrexonApp UseApiKeys<TValidator>(this TrexonApp app, string header = "X-API-Key") where TValidator : class, IApiKeyValidator => app.AddModule(new ApiKeyModule<TValidator>(header));
    /// <summary>Configures cookie authentication with API-friendly 401/403 responses.</summary>
    public static TrexonApp UseCookies(this TrexonApp app, string cookieName = "Trexon.Session") => app.AddModule(new CookieModule(cookieName));
}
