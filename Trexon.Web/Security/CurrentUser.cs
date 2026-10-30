using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Trexon.Web.Security;

/// <summary>The identity authenticated for the current HTTP request or WebSocket handshake.</summary>
public interface ICurrentUser
{
    /// <summary>Whether at least one identity is authenticated.</summary>
    bool IsAuthenticated { get; }
    /// <summary>Authenticated subject (NameIdentifier or sub); throws HTTP 401 when unavailable.</summary>
    string Id { get; }
    /// <summary>The authenticated display name, if supplied by the authentication scheme.</summary>
    string? Name { get; }
    /// <summary>The current principal; an empty anonymous principal outside a request.</summary>
    ClaimsPrincipal Principal { get; }
    /// <summary>Distinct roles using each authenticated identity's configured role claim type.</summary>
    IReadOnlyList<string> Roles { get; }
    /// <summary>Checks a role using the authenticated identities' role claim types.</summary>
    bool IsInRole(string role);
}

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public ClaimsPrincipal Principal => accessor.HttpContext?.User ?? new ClaimsPrincipal();
    public bool IsAuthenticated => Principal.Identities.Any(identity => identity.IsAuthenticated);
    private IEnumerable<ClaimsIdentity> Identities => Principal.Identities.Where(identity => identity.IsAuthenticated);
    public string Id => Identities.SelectMany(identity => new[] { identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, identity.FindFirst("sub")?.Value })
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? throw new HttpError(401, "An authenticated user identifier is required.");
    public string? Name => Identities.Select(identity => identity.Name).FirstOrDefault(name => name != null);
    public IReadOnlyList<string> Roles => Identities.SelectMany(identity => identity.FindAll(identity.RoleClaimType))
        .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.Ordinal);
}
