# Trexon.Web

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md)

Attribute-first HTTP APIs for C# / .NET 8+. Explicit route classes, async startup,
automatic validation, Swagger UI at `/docs`, and optional service modules.
Built on ASP.NET Core MVC and Kestrel. MIT license.

**Status: `1.0.0-dev`, unreleased.** The API may change before the first stable release.
No throughput advantage over Minimal APIs is claimed.

## A complete first API

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

`GET /hello/Ada` returns `{"name":"Ada"}`. Open `/docs` to try the API;
`/openapi.json` describes the same routes and models.
No base controller, assembly scanning, or explicit Swagger setup is required.

## Try the complete service

From this repository, with SDK 10 and the ASP.NET Core 8 runtime installed:

```sh
dotnet run --project examples/Trexon.Web.Sample
```

Open [localhost:5080/docs](http://localhost:5080/docs). The sample includes persistent
SQLite task CRUD, per-user ownership, registration, JWT/refresh rotation,
roles, policies, API keys, HTTPS cookie sessions with CSRF protection, custom
validation, error mappings, a custom module, output caching, CORS, rate limits,
health checks, and Trexon.Logging/Configuration/Data integration.
Follow the [sample walkthrough](examples/Trexon.Web.Sample/README.md) for requests,
admin setup, HTTPS, and all feature demonstrations. The example runs without secrets;
its automatically generated JWT key lasts only for the current process.

## Included modules

| Capability | Entry point |
| --- | --- |
| Attribute routes, DataAnnotations, problem responses, request IDs, OpenAPI | Default |
| JWT validation, local issuance, Swagger Bearer authentication | `UseJwt(settings)` |
| External OpenID Connect JWT validation | `UseJwtAuthority(authority, audience)` |
| API key validator and Swagger header authentication | `UseApiKeys<TValidator>()` |
| Secure cookies and CSRF protection | `UseCookies()` |
| SQLite or custom-store local accounts, refresh rotation and revocation | `UseUsers(databasePath)` |
| Trusted-origin CORS | `UseCors(origin)` |
| Per-client request limits | `UseRateLimit(100)` |
| Public response caching using `[OutputCache]` | `UseOutputCache()` |
| JSON dependency health reports | `UseHealthChecks()` |
| Request logs through ILogger or a custom sink | `UseRequestLogging()` |
| Typed WebSocket handlers, validation, rooms and async lifecycle | `AddSocket<THandler>()` |
| Application modules | `AddModule<T>()` |

Modules are opt-in; role and policy authorization uses standard `[Authorize]` attributes.
JWT is configured **once** for server validation and Swagger Authorize. Settings still
need a trusted issuer, audience, and a secret signing key or an external authority.
With `UseUsers()`, Swagger Authorize also offers username/password login and applies the
access token automatically. The [complete sample](examples/Trexon.Web.Sample/Program.cs)
keeps all application code and its chat client in one commented file.

## WebSocket with one handler

<!-- verify: socket-echo -->
```csharp
using Trexon.Web;
using Trexon.Web.Sockets;

var app = TrexonApp.Create(args);
app.AddSocket<EchoSocket>();
await app.RunAsync();

[Socket("/ws/echo")]
public sealed class EchoSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) =>
        connection.SendAsync(message, token);
}
```

No transport setup, framing, receive loop or JSON plumbing is required. Other payload types
use JSON automatically; `SocketHandler<TIn,TOut>` supports distinct contracts. Existing
`[Authorize]` and validators work on sockets. The [WebSocket guide](docs/WEBSOCKETS.md)
describes groups, limits and lifecycle. Open `/chat` in the full sample to try public or
cookie-authenticated private rooms. Swagger lists contracts in its WebSockets section.

## Reference from another project

The package is not published yet. Use a project reference:

```sh
dotnet new web -n Hello
dotnet add Hello/Hello.csproj reference /path/to/Trexon.Web/Trexon.Web/Trexon.Web.csproj
```

Replace `Hello/Program.cs` with the first example. Run with
`dotnet run --project Hello -- --urls http://localhost:5080`.
For a local NuGet feed, see [packaging](docs/PUBLISHING.md).

## Documentation

- [Russian handbook](docs/README.md): start, routes, validation, security, modules, operations.
- [English guide](docs/en/README.md) and [German guide](docs/de/README.md).
- [Minimal APIs comparison and migration](docs/MINIMAL_APIS.md).
- [Public API reference](docs/API.md).
- [Architecture and async lifecycle](docs/ARCHITECTURE.md).
- [Development and verification](docs/DEVELOPMENT.md).
- [Current boundaries and roadmap](docs/ROADMAP.md).

All I/O-facing APIs are asynchronous and carry cancellation tokens. Configuration
and CPU-only work remain synchronous. Microsoft.Data.Sqlite exposes async methods
but executes database I/O synchronously; use a custom asynchronous `IUserStore` for
a network database when that distinction matters. Native AOT/trimming is not supported.

## Verify

```sh
dotnet build Trexon.Web.slnx --configuration Release -warnaserror
dotnet test --solution Trexon.Web.slnx --configuration Release --no-build --no-restore
python3 scripts/check-docs.py
python3 scripts/smoke-sample.py
dotnet pack Trexon.Web/Trexon.Web.csproj --configuration Release --no-build --output artifacts
```

The HTTP integration suite runs on .NET 8 and .NET 10. The smoke test starts the sample
on a temporary loopback port, exercises its real HTTP API, stops it, and deletes test data.
