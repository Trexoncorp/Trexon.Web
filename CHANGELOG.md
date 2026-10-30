# Changelog

## Trexon.Templates 1.0.0 — 2026-10-30

- Added the separate Trexon.Templates 1.0.0 package: `dotnet new trexon-api`
  generates a standalone notes API with Swagger, validated CRUD, SQLite,
  typed environment configuration, structured logs and HTTP request examples.
- Added isolated template installation/generation and real HTTP/restart checks to CI.

## 1.0.1 — 2026-10-30

- Added `ApiOptions.Prefix` to mount the entire service, including docs, auth,
  health endpoints, HTTP routes and WebSockets, under a path such as `/v1`.
- Added `AddRoutes<T>(prefix)`, `AddRoutes(Type, prefix)` and `AddSocket<T>(prefix)`
  to prefix individual route groups without editing their attributes. Prefixes can
  be changed or cleared before building; plain registration preserves the configured prefix.
- Preserved prefixes for absolute MVC action routes, validation, authorization,
  Swagger password login and single-use WebSocket console tickets.
- OpenAPI now describes the effective application/proxy mount in `servers`.
  Problem Details, request logs and generated sample links retain the public path.
- Updated the calculator comments, compiled prefix walkthrough and package consumer
  smoke test. Existing registrations and URLs keep their behavior with empty prefixes.

## 1.0.0 — 2026-10-30

First stable release of Trexon.Web for small C# HTTP and WebSocket services.

- Explicit route classes with HTTP, tags, summaries, response and security attributes.
- Swagger UI at `/docs`, OpenAPI, automatic binding/DataAnnotations validation,
  asynchronous custom validators, Problem Details and request IDs.
- Async host lifecycle, sequential module initialization and `RunAsync(port)` shortcuts.
- Local or external JWT, API keys, secure cookies with CSRF protection, roles and policies.
- Optional SQLite-backed accounts: registration, login, refresh rotation/reuse detection,
  logout and replaceable `IUserStore`. Username/password login in Swagger Authorize.
- CORS, per-client rate limits, public output caching and dependency health checks.
- Automatic request logging through `Trexon.Logging.Log.Info`, with custom sync/async sinks.
- Typed WebSocket handlers, JSON, validation, lifecycle, rooms, limits and shared authorization.
- Built-in English WebSocket console at `/docs/ws` with password, Bearer, API key and cookie
  authentication. Browser credentials use short-lived, single-use connection tickets.
- A small calculator and a complete task service, each in one commented `Program.cs`.
- Russian handbook, English and German guides, compiled documentation examples and
  HTTP/WebSocket integration tests on .NET 8 and .NET 10.

Compatibility: the package targets `net8.0` and requires the ASP.NET Core shared framework.
Public 1.x APIs follow semantic versioning; breaking changes require a new major version.
The development builds were unpublished and are not a separate compatibility baseline.

Boundaries: MVC uses reflection; Native AOT/trimming is not supported. No measured speed
advantage over Minimal APIs is claimed. Local accounts are not an OAuth/OIDC identity
server and do not include MFA, password reset or email verification. Logout revokes refresh
families; existing access tokens expire normally. Limits, caches, rooms and console tickets
are process-local. Microsoft.Data.Sqlite executes database I/O synchronously internally.
See the [complete boundaries](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/ROADMAP.md) and [security guide](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/SECURITY.md).
