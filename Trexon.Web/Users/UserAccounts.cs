using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Trexon.Web.Security;

namespace Trexon.Web.Users;

internal sealed class UserAccounts(IUserStore store, IPasswordHasher<StoredUser> hasher,
    ITokenIssuer issuer, UsersOptions options, TimeProvider time) : IUserAccounts
{
    // One shared dummy hash keeps unknown-user logins on the expensive verification path.
    private readonly string _dummyHash = hasher.HashPassword(new("", "", "", []), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    public async Task<UserInfo> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        if (!options.AllowRegistration) throw new HttpError(403, "Registration is disabled.");
        var user = new StoredUser(Guid.NewGuid().ToString("N"), request.Username, "", options.RegistrationRoles.ToArray());
        user = user with { PasswordHash = hasher.HashPassword(user, request.Password) };
        if (!await store.TryCreateAsync(user, cancellationToken)) throw new HttpError(409, "Username is already registered.");
        return Info(user);
    }
    public async Task<UserInfo> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await Verify(request, cancellationToken); return Info(user);
    }
    private async Task<StoredUser> Verify(LoginRequest request, CancellationToken token)
    {
        Validate(request);
        var user = await store.FindByNameAsync(request.Username, token);
        var result = hasher.VerifyHashedPassword(user ?? new("", "", "", []), user?.PasswordHash ?? _dummyHash, request.Password);
        if (user == null || result == PasswordVerificationResult.Failed) throw new HttpError(401, "Invalid username or password.");
        return user;
    }
    public async Task<TokenPair> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await Verify(request, cancellationToken);
        var refresh = NewRefresh(); var expires = time.GetUtcNow().Add(options.RefreshLifetime);
        await store.AddSessionAsync(new(Hash(refresh), user.Id, Guid.NewGuid().ToString("N"), expires), cancellationToken);
        return Pair(user, refresh, expires);
    }
    public async Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 256) throw new HttpError(401, "Invalid refresh token.");
        var next = NewRefresh();
        var user = await store.RotateAsync(Hash(refreshToken), Hash(next), time.GetUtcNow(), cancellationToken);
        if (user == null) throw new HttpError(401, "Refresh token expired, revoked, or already used.");
        var expiry = await store.GetSessionExpiryAsync(Hash(next), cancellationToken);
        if (expiry == null) throw new InvalidOperationException("The account store did not persist the rotated session.");
        return Pair(user, next, expiry.Value);
    }
    public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default) => store.RevokeAsync(Hash(refreshToken), cancellationToken);
    private TokenPair Pair(StoredUser user, string refresh, DateTimeOffset refreshExpires)
    {
        var access = issuer.Issue(user.Id, user.Username, user.Roles);
        return new(access.Token, access.ExpiresAt, refresh, refreshExpires, Info(user));
    }
    private static void Validate(object model)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(model, new ValidationContext(model), results, true))
            throw new HttpError(400, string.Join(" ", results.Select(r => r.ErrorMessage)));
    }
    private static UserInfo Info(StoredUser user) => new(user.Id, user.Username, user.Roles.ToArray());
    private static string NewRefresh() => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
