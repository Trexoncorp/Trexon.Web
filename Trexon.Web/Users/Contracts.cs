using System.ComponentModel.DataAnnotations;

namespace Trexon.Web.Users;

/// <summary>Settings for the optional local user-account module.</summary>
public sealed class UsersOptions
{
    /// <summary>Route prefix for account endpoints.</summary>
    public string Prefix { get; set; } = "/auth";
    /// <summary>Whether unauthenticated clients can register accounts.</summary>
    public bool AllowRegistration { get; set; } = true;
    /// <summary>Roles granted by the server at registration. Client input never controls roles.</summary>
    public string[] RegistrationRoles { get; set; } = ["User"];
    /// <summary>Refresh-token lifetime, default seven days. Rotation keeps the family's original expiry.</summary>
    public TimeSpan RefreshLifetime { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>A new local account.</summary>
public sealed class RegisterRequest
{
    /// <summary>Unique, case-insensitive account name.</summary>
    [Required, StringLength(64, MinimumLength = 3), RegularExpression("^[a-zA-Z0-9_.-]+$")]
    public string Username { get; set; } = "";
    /// <summary>Password with 12 to 128 characters.</summary>
    [Required, StringLength(128, MinimumLength = 12)]
    public string Password { get; set; } = "";
}
/// <summary>Credentials used for JWT or cookie login.</summary>
public sealed class LoginRequest
{
    /// <summary>The registered account name.</summary>
    [Required, StringLength(64)] public string Username { get; set; } = "";
    /// <summary>The account password.</summary>
    [Required, StringLength(128)] public string Password { get; set; } = "";
}
/// <summary>A refresh or logout request.</summary>
public sealed class RefreshRequest
{
    /// <summary>The opaque refresh token returned at login.</summary>
    [Required, StringLength(256, MinimumLength = 32)] public string RefreshToken { get; set; } = "";
}
/// <summary>Public account data; never includes password or token hashes.</summary>
public sealed record UserInfo(string Id, string Username, string[] Roles);
/// <summary>A local access/refresh token pair.</summary>
public sealed record TokenPair(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt, UserInfo User);
/// <summary>An internal account-store record. Do not return it from HTTP handlers.</summary>
public sealed record StoredUser(string Id, string Username, string PasswordHash, string[] Roles);
/// <summary>A hashed refresh-token record.</summary>
public sealed record RefreshSession(string TokenHash, string UserId, string FamilyId, DateTimeOffset ExpiresAt);

/// <summary>Persistent user storage. Rotation must atomically consume a token and revoke its family on reuse.</summary>
public interface IUserStore
{
    /// <summary>Initializes the store before accepting requests. Must be idempotent.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    /// <summary>Creates a user if its normalized name is not already registered.</summary>
    Task<bool> TryCreateAsync(StoredUser user, CancellationToken cancellationToken = default);
    /// <summary>Looks up a user by case-insensitive name.</summary>
    Task<StoredUser?> FindByNameAsync(string username, CancellationToken cancellationToken = default);
    /// <summary>Looks up a user by ID.</summary>
    Task<StoredUser?> FindByIdAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>Updates trusted server-assigned roles and revokes refresh sessions.</summary>
    Task SetRolesAsync(string id, string[] roles, CancellationToken cancellationToken = default);
    /// <summary>Persists a new refresh-token family.</summary>
    Task AddSessionAsync(RefreshSession session, CancellationToken cancellationToken = default);
    /// <summary>Rotates a still-valid token atomically. Returns null on expiry, revocation, or reuse.</summary>
    Task<StoredUser?> RotateAsync(string oldHash, string nextHash, DateTimeOffset now, CancellationToken cancellationToken = default);
    /// <summary>Returns a persisted session's expiry.</summary>
    Task<DateTimeOffset?> GetSessionExpiryAsync(string tokenHash, CancellationToken cancellationToken = default);
    /// <summary>Revokes the token's entire refresh family.</summary>
    Task RevokeAsync(string tokenHash, CancellationToken cancellationToken = default);
}

/// <summary>Local account operations, also reusable from custom route classes.</summary>
public interface IUserAccounts
{
    /// <summary>Registers an account with server-controlled initial roles.</summary>
    Task<UserInfo> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    /// <summary>Verifies credentials and issues a JWT/refresh pair.</summary>
    Task<TokenPair> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    /// <summary>Verifies credentials without issuing tokens, for cookie login.</summary>
    Task<UserInfo> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
    /// <summary>Consumes a refresh token and issues a replacement.</summary>
    Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
    /// <summary>Revokes a refresh family. Issued access tokens retain their short expiry.</summary>
    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);
}
