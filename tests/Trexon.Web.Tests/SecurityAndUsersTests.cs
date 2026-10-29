using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Trexon.Web.Security;
using Trexon.Web.Users;

namespace Trexon.Web.Tests;

public sealed class SecurityAndUsersTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Jwt_distinguishes_anonymous_forbidden_and_authorized_requests()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).AddRoutes<AccessRoutes>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/access/admin", Token)).StatusCode);
        var issuer = host.App.Services.GetRequiredService<ITokenIssuer>();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", issuer.Issue("user", roles: ["User"]).Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/access/admin", Token)).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", issuer.Issue("admin", roles: ["Admin"]).Token);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/access/admin", Token)).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-token");
        var invalid = await host.Client.GetAsync("/access/admin", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode); Assert.NotEmpty(invalid.Headers.WwwAuthenticate);
        Assert.NotNull((await invalid.Content.ReadFromJsonAsync<JsonObject>(Token))!["traceId"]);
    }
    [Theory]
    [InlineData("bad-issuer", "trexon-tests", false, false)]
    [InlineData("trexon-tests", "bad-audience", false, false)]
    [InlineData("trexon-tests", "trexon-tests", true, false)]
    [InlineData("trexon-tests", "trexon-tests", false, true)]
    public async Task Jwt_rejects_wrong_issuer_audience_expiry_and_signature(string issuer, string audience, bool expired, bool wrongKey)
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).AddRoutes<AccessRoutes>());
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(issuer, audience, [new("sub", "subject"), new("role", "Admin")], now.AddHours(-1),
            expired ? now.AddMinutes(-10) : now.AddMinutes(10),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(wrongKey ? new string('x', 48) : TestHost.Jwt.SigningKey)), SecurityAlgorithms.HmacSha256));
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/access/admin", Token)).StatusCode);
    }
    [Fact]
    public async Task Policies_evaluate_claims()
    {
        await using var host = await TestHost.Start(app =>
        {
            app.UseJwt(TestHost.Jwt).AddRoutes<AccessRoutes>();
            app.Services.AddAuthorization(o => o.AddPolicy("reports", p => p.RequireClaim("permission", "reports:read")));
        });
        var issuer = host.App.Services.GetRequiredService<ITokenIssuer>();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", issuer.Issue("reader", claims: [new("permission", "reports:read")]).Token);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/access/reports", Token)).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", issuer.Issue("reader").Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/access/reports", Token)).StatusCode);
    }
    [Fact]
    public async Task Api_keys_and_jwt_can_coexist_with_explicit_schemes()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).UseApiKeys<TestApiKeys>().AddRoutes<AccessRoutes>());
        host.Client.DefaultRequestHeaders.Add("X-API-Key", "valid-test-key");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/access/key", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/access/any", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/access/jwt", Token)).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-API-Key"); host.Client.DefaultRequestHeaders.Add("X-API-Key", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/access/key", Token)).StatusCode);
    }
    [Fact]
    public async Task Swagger_security_matches_anonymous_and_named_authentication()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).UseApiKeys<TestApiKeys>().AddRoutes<AccessRoutes>());
        var doc = await host.Json("/openapi.json");
        Assert.NotNull(doc["components"]!["securitySchemes"]!["Bearer"]);
        Assert.NotNull(doc["components"]!["securitySchemes"]!["ApiKey"]);
        Assert.Contains("ApiKey", doc["paths"]!["/access/key"]!["get"]!["security"]!.ToJsonString());
        Assert.DoesNotContain("Bearer", doc["paths"]!["/access/key"]!["get"]!["security"]!.ToJsonString());
        Assert.Null(doc["paths"]!["/access/public"]!["get"]!["security"]);
    }
    [Fact]
    public async Task Accounts_register_login_and_get_current_user_without_exposing_hashes()
    {
        await using var host = await TestHost.Start(users: true);
        var pair = await host.RegisterLogin();
        Assert.Equal(["User"], pair.User.Roles);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", pair.AccessToken);
        var me = await host.Client.GetStringAsync("/auth/me", Token);
        Assert.Contains("tester", me); Assert.DoesNotContain("password", me, StringComparison.OrdinalIgnoreCase);
        var duplicate = await host.Client.PostAsJsonAsync("/auth/register", new { username = "TESTER", password = "long-test-password-123" }, Token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var invalid = await host.Client.PostAsJsonAsync("/auth/login", new { username = "tester", password = "wrong-password-123" }, Token);
        var unknown = await host.Client.PostAsJsonAsync("/auth/login", new { username = "unknown", password = "wrong-password-123" }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal((await invalid.Content.ReadFromJsonAsync<JsonObject>(Token))!["detail"]!.ToString(),
            (await unknown.Content.ReadFromJsonAsync<JsonObject>(Token))!["detail"]!.ToString());
    }
    [Fact]
    public async Task Registration_validates_password_and_does_not_accept_roles()
    {
        await using var host = await TestHost.Start(users: true);
        var bad = await host.Client.PostAsJsonAsync("/auth/register", new { username = "tester", password = "short" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var good = await host.Client.PostAsJsonAsync("/auth/register", new { username = "tester", password = "long-test-password-123", roles = new[] { "Admin" } }, Token);
        var user = await good.Content.ReadFromJsonAsync<UserInfo>(Token);
        Assert.Equal(["User"], user!.Roles);
        var store = host.App.Services.GetRequiredService<IUserStore>();
        var stored = await store.FindByIdAsync(user.Id, Token);
        Assert.NotEqual("long-test-password-123", stored!.PasswordHash);
    }
    [Fact]
    public async Task Refresh_rotates_once_and_reuse_revokes_the_replacement_family()
    {
        await using var host = await TestHost.Start(users: true);
        var first = await host.RegisterLogin();
        var refresh = await host.Client.PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken }, Token);
        var next = (await refresh.Content.ReadFromJsonAsync<TokenPair>(Token))!;
        Assert.NotEqual(first.RefreshToken, next.RefreshToken);
        Assert.True(next.RefreshExpiresAt <= first.RefreshExpiresAt);
        var reuse = await host.Client.PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var revoked = await host.Client.PostAsJsonAsync("/auth/refresh", new { refreshToken = next.RefreshToken }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }
    [Fact]
    public async Task Logout_revokes_refresh_tokens_while_access_tokens_keep_their_expiry()
    {
        await using var host = await TestHost.Start(users: true);
        var pair = await host.RegisterLogin();
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PostAsJsonAsync("/auth/logout", new { refreshToken = pair.RefreshToken }, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync("/auth/refresh", new { refreshToken = pair.RefreshToken }, Token)).StatusCode);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", pair.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/auth/me", Token)).StatusCode);
    }
    [Fact]
    public async Task Role_changes_revoke_existing_refresh_sessions()
    {
        await using var host = await TestHost.Start(users: true);
        var pair = await host.RegisterLogin();
        await host.App.Services.GetRequiredService<IUserStore>().SetRolesAsync(pair.User.Id, ["Admin"], Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync("/auth/refresh", new { refreshToken = pair.RefreshToken }, Token)).StatusCode);
    }
    [Fact]
    public async Task Cookie_login_requires_csrf_and_creates_a_secure_http_only_session()
    {
        await using var host = await TestHost.Start(app => app.UseCookies().AddRoutes<AccessRoutes>(), users: true);
        await host.RegisterLogin();
        var payload = new { username = "tester", password = "long-test-password-123" };
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/auth/cookie/login", payload, Token)).StatusCode);
        var csrfResponse = await host.Client.GetAsync("/auth/cookie/csrf", Token);
        var csrf = (await csrfResponse.Content.ReadFromJsonAsync<JsonObject>(Token))!["requestToken"]!.ToString();
        var csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        host.Client.DefaultRequestHeaders.Add("Cookie", csrfCookie);
        host.Client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        var login = await host.Client.PostAsJsonAsync("/auth/cookie/login", payload, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var authCookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        host.Client.DefaultRequestHeaders.Remove("Cookie"); host.Client.DefaultRequestHeaders.Add("Cookie", csrfCookie + "; " + authCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/auth/me", Token)).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync("/access/write", null, Token)).StatusCode);
        // CSRF request tokens bind to the signed-in identity; fetch a new token after login.
        var authenticatedCsrf = await host.Client.GetFromJsonAsync<JsonObject>("/auth/cookie/csrf", Token);
        host.Client.DefaultRequestHeaders.Add("X-CSRF-Token", authenticatedCsrf!["requestToken"]!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync("/access/write", null, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PostAsync("/auth/cookie/logout", null, Token)).StatusCode);
    }
    [Fact]
    public async Task Cookie_endpoints_are_absent_without_cookie_module()
    {
        await using var host = await TestHost.Start(users: true);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/auth/cookie/csrf", Token)).StatusCode);
    }
    [Fact]
    public async Task Accounts_can_be_mounted_under_a_custom_prefix_with_registration_disabled()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt).UseUsers(path, options => { options.Prefix = "/identity"; options.AllowRegistration = false; }));
            var response = await host.Client.PostAsJsonAsync("/identity/register", new { username = "tester", password = "long-test-password-123" }, Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync("/auth/register", new { }, Token)).StatusCode);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    [Fact]
    public void Invalid_security_configuration_fails_early()
    {
        Assert.Throws<ArgumentException>(() => TrexonApp.Create().UseJwt(new("issuer", "audience", "short")));
        Assert.Throws<InvalidOperationException>(() => TrexonApp.Create().UseUsers("unused.db"));
        Assert.Throws<ArgumentException>(() => TrexonApp.Create().UseJwtAuthority("http://insecure.example", "api"));
    }
    [Fact]
    public async Task Caller_cannot_override_reserved_jwt_claims()
    {
        await using var host = await TestHost.Start(app => app.UseJwt(TestHost.Jwt));
        var issuer = host.App.Services.GetRequiredService<ITokenIssuer>();
        Assert.Throws<ArgumentException>(() => issuer.Issue("user", claims: [new("role", "Admin")]));
    }
}

[Routes("/access"), Authorize]
public sealed class AccessRoutes
{
    [Get("admin"), Authorize(Roles = "Admin")] public string Admin() => "admin";
    [Get("reports"), Authorize(Policy = "reports")] public string Reports() => "reports";
    [Get("key"), Authorize(AuthenticationSchemes = AuthSchemes.ApiKey)] public string Key() => "key";
    [Get("jwt"), Authorize(AuthenticationSchemes = AuthSchemes.Bearer)] public string Jwt() => "jwt";
    [Get("any")] public string Any() => "any";
    [Get("public"), AllowAnonymous] public string Public() => "public";
    [Post("write")] public string Write() => "written";
}
public sealed class TestApiKeys : IApiKeyValidator
{
    public ValueTask<ClaimsPrincipal?> ValidateAsync(string key, CancellationToken token) => ValueTask.FromResult(key == "valid-test-key"
        ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "integration"), new Claim(ClaimTypes.Name, "integration")], AuthSchemes.ApiKey)) : null);
}
