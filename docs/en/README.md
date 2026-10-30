# Trexon.Web guide

[Home](../../README.md) · [Русское руководство](../README.md) · [Deutsch](../de/README.md)

Trexon.Web provides explicit attribute routes and reusable service modules on top of
ASP.NET Core MVC/Kestrel. This guide describes the stable `1.0.0` API. It does
not claim higher throughput than Minimal APIs or Native AOT compatibility.


With `UseUsers()`, Swagger **Authorize** includes username/password login and applies the
Bearer token automatically. Credentials are not persisted; reload or token expiry requires
a new login. The complete sample keeps its code and embedded chat page in one `Program.cs`.

## Start with the runnable example

Install SDK 10 and ASP.NET Core runtime 8. From the repository root:

```sh
dotnet run --project examples/Trexon.Web.Sample
```

Open `http://localhost:5080/docs`; `/openapi.json` exposes the schema. The complete
[sample walkthrough](../../examples/Trexon.Web.Sample/README.md) includes registration,
per-user SQLite task CRUD, JWT/refresh tokens, admin roles, policies, API keys, cookies,
CSRF, validation, errors, caching, CORS, rate limits, and health checks.

A process-local JWT key is generated if none is configured. To enable all sample
credentials, set `TREXON_JWT_KEY`, `TREXON_API_KEY`, and `TREXON_ADMIN_PASSWORD` before
starting; use random secrets. `TREXON_DATA_DIR` selects durable storage and `TREXON_URL`
selects the listener. The sample consumes Trexon.Logging, Configuration, and Data from NuGet.

## First independent application

Create an ASP.NET Core application with `dotnet new web -n Hello`, then install
`dotnet add Hello/Hello.csproj package Trexon.Web --version 1.0.0`. Replace Program.cs:

<!-- verify: first-api -->
```csharp
using Trexon.Web;

var app = TrexonApp.Create(args);
app.AddRoutes<GreetingRoutes>();
await app.RunAsync();

[Routes("/hello"), Tag("Greetings")]
public sealed class GreetingRoutes
{
    [Get("{name}"), Summary("Greet a visitor")]
    public ValueTask<Greeting> Get(string name) => ValueTask.FromResult(new Greeting(name));
}

public sealed record Greeting(string Name);
```

`dotnet run --project Hello -- --urls http://localhost:5080` starts the service.
`GET /hello/Ada` returns `{"name":"Ada"}`. No base controller or assembly scanning is needed.
Only types registered with `AddRoutes<T>` become routes.

## Routing and responses

`[Routes("/products")]` declares the prefix. Methods use `[Get]`, `[Post]`, `[Put]`,
`[Patch]`, `[Delete]`, `[Head]`, or `[Options]`. Standard MVC HTTP attributes also work.
Public helper methods without an HTTP attribute are not endpoints. Handlers must be
public, non-generic instance methods without ref/out parameters.

Use constructor injection for scoped services. Simple arguments matching route
parameters come from the route; remaining simple arguments come from query strings.
Complex arguments use MVC API-controller inference, normally a JSON body. Explicit
`[FromQuery]`, `[FromBody]`, `[FromHeader]`, `[FromServices]`, and other standard attributes
resolve ambiguity. `CancellationToken` is the request-aborted token.

Return `Task<T>` for I/O or `ValueTask<T>` for immediately available results. Object
responses are JSON; plain strings use MVC text formatting. Use `IActionResult` or
`ActionResult<T>` for status codes/headers: CreatedResult, NoContentResult, FileResult.
`[Response(201, typeof(Product))]` documents the response; it does not change the runtime status.
`[Tag]` groups operations and `[Summary]` supplies a summary and optional Markdown Description.

The builder checks obvious duplicate verb/template routes, including equivalent
parameter names. Overlapping constraints/catch-all patterns still follow ASP.NET routing
and need HTTP tests. A class with `[Authorize]` requires an authentication scheme.

## Validation and errors

DataAnnotations run automatically, including nested models and parameter constraints.
Malformed JSON, binding failures, and invalid models return 400 with field-level `errors`.
Register `app.AddValidator<T, TValidator>()` for an additional scoped
`IRequestValidator<T>`; its `ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(T, CancellationToken)`
can await business checks. Multiple validators run sequentially after normal model validation.
They apply to handler arguments, not every nested object automatically.

Exceptions are caught before response headers start. Unexpected failures return 500
without internal details and are logged through ILogger. Throw `HttpError(status, safeDetail)`
for intentional errors or configure `app.Errors(e => e.Map<MyException>(404, "Not found"))`.
Mappings do not expose exception messages. Replace `IExceptionResponder` in DI for
custom exception responses. `IncludeExceptionDetails` is false in every environment by default.

Empty error responses, including 404/405/401/403, use Problem Details. Responses carry
`X-Request-ID`; error `traceId` uses the active Activity ID or request ID. A caller-supplied
request ID is accepted only for 1–64 ASCII letters/digits/hyphens/underscores.
Request cancellation is not converted into a fake 500, and an already-started response
cannot be rewritten as a new error body.

## Authentication and authorization

`app.UseJwt(new JwtSettings(issuer, audience, signingKey))` configures signature, issuer,
audience, expiry validation, local token issuance, and Swagger Bearer authorization in
one call. Supply a random secret of at least 32 UTF-8 bytes. HS256 is used, access tokens
last 15 minutes, and clock skew is 30 seconds by default. `ITokenIssuer` is available
through DI for trusted server-side issuance. Reserved identity/role/lifetime claims
cannot be overridden by additional claims.

For an existing identity provider, use `UseJwtAuthority("https://identity.example", audience)`.
The standard handler retrieves OIDC metadata/keys. Local issuance is not enabled in
this mode. Choose one Bearer module; local and authority variants share the same scheme name.

Use standard `[Authorize]`, `[Authorize(Roles = "Admin")]`, `[Authorize(Policy = "reports")]`,
and `[AllowAnonymous]`. Policies and authorization handlers are registered with standard
ASP.NET Core DI. 401 means authentication is missing/invalid; 403 means permission is missing.

`UseApiKeys<MyValidator>()` registers a scoped `IApiKeyValidator` and Swagger's API-key
scheme. The default header is `X-API-Key`; override its name in the call. Return an
authenticated ClaimsPrincipal or null. Key persistence, expiry, and revocation belong
to the application. `[Authorize(AuthenticationSchemes = AuthSchemes.ApiKey)]` restricts
a route to keys; AuthSchemes.Bearer restricts it to JWT.

When several schemes coexist, an ordinary Authorize selects a supplied Bearer header,
then an API-key header, then cookies, then the first registered scheme. Invalid explicitly
supplied Bearer credentials do not silently fall back to a cookie. Swagger lists the
registered alternatives, respecting explicit AuthenticationSchemes. Complex policy logic
cannot be completely represented in OpenAPI; document it in the operation description.

`UseCookies()` registers Secure, HTTP-only, SameSite=Lax cookies, eight-hour sliding
sessions, and API 401/403 responses instead of HTML redirects. Cookie mutations require
antiforgery validation. With local accounts, the built-in flow is:

1. Over HTTPS, GET `/auth/cookie/csrf`, retain its antiforgery cookie and requestToken.
2. POST `/auth/cookie/login` with credentials, the cookie, and X-CSRF-Token.
3. Fetch a new CSRF request token after sign-in; tokens are identity-bound.
4. Send it on cookie-authenticated POST/PUT/PATCH/DELETE and cookie logout.

Swagger can send an already-established same-origin HTTP-only cookie, but Authorize
cannot set that cookie manually. Automatic CSRF injection into Swagger UI is not included.
Bearer and API-key requests do not need CSRF when no cookie identity is used.
Custom cookie login handlers must validate antiforgery before signing in.

## Local users and refresh tokens

Call `UseJwt(settings)` before `UseUsers("data/accounts.db")`. Optional UseCookies adds
cookie endpoints. Registration accepts username (3–64 ASCII letters/digits/_.-) and
password (12–128 characters). PasswordHasher uses 210000 iterations. Registration
never accepts caller-controlled roles; the default server role is User.

| Endpoint | Contract |
| --- | --- |
| POST `/auth/register` | username/password → 201 UserInfo |
| POST `/auth/login` | username/password → TokenPair |
| POST `/auth/refresh` | refreshToken → replacement TokenPair |
| POST `/auth/logout` | refreshToken → 204 |
| GET `/auth/me` | authenticated local user → UserInfo |

TokenPair contains accessToken/expiresAt, refreshToken/refreshExpiresAt, and a public
user object. Password hashes are never returned. Refresh tokens use 48 random bytes;
only SHA-256 hashes are persisted. Rotation is transactional and single-use. Reusing
an old refresh token revokes its whole family, including its current replacement.
Clients should serialize refresh requests. The default seven-day family expiry does
not extend on rotation. Logout and role changes revoke refresh sessions; previously
issued access JWTs and cookie sessions remain valid until their own expiry.

Use UsersOptions to change Prefix, AllowRegistration, RegistrationRoles, or RefreshLifetime.
For a custom database, implement a concurrent-safe singleton IUserStore and call UseUsers()
without a path. InitializeAsync must be idempotent; RotateAsync must atomically consume,
replace, and revoke-on-reuse. SQLite is supplied; a network store is not bundled.
No MFA, password reset, email verification, lockout counter, or background refresh cleanup
is implemented. This module is not an OAuth/OIDC identity server.

## Modules and async startup

An ITrexonModule has synchronous Configure for DI/routes, optional synchronous Use to
construct middleware, and optional ValueTask InitializeAsync for awaited startup I/O.
Modules initialize sequentially before the listener starts. Duplicate module types fail.
Register parameterless modules with AddModule<T>() or configured instances with AddModule(instance).

Other modules: UseCors(explicitOrigin), UseRateLimit(permits, window), UseOutputCache(),
and UseHealthChecks(path). Every completed request is logged through `Trexon.Logging.Log.Info`
by default, using the application’s `Log.Configure` settings. `UseRequestLogging(sink)`
replaces that default with a custom sink. Rate limits are per user ID or remote IP,
fixed-window, in-process, with no queue. OutputCache uses standard public-response policies;
authenticated responses are not cached by default. Add standard IHealthCheck registrations
for dependencies. Logging excludes body/query/credentials and can forward to a custom
synchronous or awaited asynchronous sink.

`await app.RunAsync(token)` owns host startup and disposal. For explicit ownership:
`await using var web = await app.BuildAsync(token)`, then use standard StartAsync/RunAsync.
BuildAsync awaits all initializers and disposes on failure. No sync-over-async or background
job scheduler is involved. Configuration and CPU-only operations remain synchronous.
Microsoft.Data.Sqlite executes async database calls synchronously internally; use a
network-driver IUserStore for true asynchronous database I/O.

`app.Builder`, Services, and Configuration expose normal ASP.NET Core integration points.
`app.Use(...)` adds middleware after security; `app.Map(...)` can add Minimal API endpoints.
MVC validation/custom validators apply to route classes, not arbitrary Minimal API handlers.

## Verification, deployment, and deeper references

Run the commands from [Development](../DEVELOPMENT.md). CI builds, runs tests for .NET 8
and 10, compiles complete documentation snippets, checks local links, exercises the real
sample, and packs NuGet artifacts. Publishing is separate. Use the [operations guide](../OPERATIONS.md)
for TLS/proxy trust, stable secrets, SQLite backups, persistent Data Protection keys,
and process-local limits/cache. The [API reference](../API.md), [architecture](../ARCHITECTURE.md),
[comparison](../MINIMAL_APIS.md), and [boundaries](../ROADMAP.md) provide detailed contracts.

## High-level WebSockets

`app.AddSocket<ChatSocket>()` enables its `[Socket("/ws/chat/{room}")]` endpoint and
transport automatically. Derive from `SocketHandler<T>` or `SocketHandler<TIn,TOut>`
and implement `ValueTask OnMessageAsync(TIn, SocketConnection<TOut>, CancellationToken)`.
String messages are plain text; other contracts use JSON. The library owns framing,
fragment assembly, validation, cancellation, bounded sends and the closing handshake.

Use connection.SendAsync for a reply, Join/Leave for group membership, and BroadcastAsync
for endpoint-local or group delivery, including the sender. Different endpoints have
isolated registries. Connect/disconnect callbacks are optional. Handlers and their scoped
services live for the connection. Existing DataAnnotations and AddValidator registrations
run before each message; invalid JSON/models receive a reserved JSON error envelope.
Unexpected failures close with 1011 without internal details.

Authorize/roles/policies are checked before upgrade. Native clients can send credential
headers; browsers use an HTTPS same-origin cookie session. Origin is checked independently
of CORS: same-origin is allowed by default, additional trusted origins are explicit.
Missing Origin is allowed for native clients unless disabled. Query-string JWT extraction
is not enabled. Authorization is a handshake snapshot, not a per-message revalidation.

Optional UseWebSockets configures defaults: 64 KiB per complete message, 120 messages per
minute per connection, two-minute transport keep-alive, ten-second send timeout, five-second
close timeout. Limits/cache remain process-local. There is no distributed backplane,
durable message store, binary streaming or implicit reconnection.

Swagger's WebSockets section lists contracts; x-trexon-websockets in OpenAPI contains paths,
formats, auth metadata and input/output schema references. HTTP Try it out does not exchange
socket messages. `/chat` in the complete sample demonstrates public/private rooms and a
browser cookie login. [Detailed contract and complete examples](../WEBSOCKETS.md).

The built-in **WebSocket console** at `/docs/ws` provides endpoint selection, route
parameters, schema-based JSON examples, send/receive logs and validation errors.
Username/password login, Bearer tokens and API keys are supported through short-lived,
single-use connection tickets. The original endpoint still validates schemes, roles and policies.
Cookie sign-in is also available with `UseUsers()` + `UseCookies()` over HTTPS. The page follows
`DocsPath` and reverse-proxy PathBase, and is disabled with `EnableDocs = false`.

A small [calculator example](../../examples/Trexon.Web.Calculator/README.md) keeps the
whole API in one Program.cs. Run it with `dotnet run --project examples/Trexon.Web.Calculator`.
Both `TrexonApp` and a built `WebApplication` accept `await web.RunAsync(5081)`: HTTP listens
on `127.0.0.1:5081`, overriding configured listening addresses. The no-port overload retains
normal host configuration, including HTTPS. Port zero selects an available port.
