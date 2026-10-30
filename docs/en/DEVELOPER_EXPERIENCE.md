# Developer experience in 1.1

[Handbook](README.md) · [Complete runnable examples (Russian)](../DEVELOPER_EXPERIENCE.md)

## In-process tests

Reference `Trexon.Web.Testing` 1.1.0 from your test project. Production applications
only need Trexon.Web. The test package targets .NET 8 and .NET 10, using the matching
ASP.NET Core TestHost. No TCP port is opened and the real middleware, validation and
authentication pipeline runs.

Call `await TrexonTest.CreateAsync(ConfigureApi)` where ConfigureApi is the same
`Action<TrexonApp>` registration method used by production. `api.Client` is an HttpClient
with BaseAddress http://localhost. `api.CreateWebSocketClient()` connects to the same
in-process app. Close sockets before disposing the application with `await using`.
CreateAsync and all client operations accept cancellation tokens.

The optional `overrides` delegate receives IServiceCollection after application
registration, before BuildAsync and startup. Use the standard `services.Replace(...)`
to substitute a fake. `api.Services` is the root provider: use CreateAsyncScope to
resolve scoped services. Instances supplied by the caller remain caller-owned.
The helper does not bypass authentication: send actual JWTs/cookies/API keys.
For cookie tests, set an HTTPS BaseAddress; TestServer does not perform a TLS handshake.
Build/start failures dispose the built host and repeated DisposeAsync is safe.

## Current user through DI

`Trexon.Web.Security.ICurrentUser` is registered as scoped automatically. Inject it
in route/service/socket constructors. IsAuthenticated checks authenticated identities;
Id returns NameIdentifier or sub and throws HTTP 401 if no authenticated, non-empty
subject exists. Name is optional. Roles and IsInRole use authenticated identities'
configured RoleClaimType. Principal exposes the underlying ClaimsPrincipal.
Anonymous claims do not confer a user ID or roles. Outside a request, IsAuthenticated
is false and Id throws 401. Authorization still uses [Authorize], roles and policies.
A WebSocket retains its handshake identity; tokens are not refreshed per message.

## Scoped asynchronous startup

Implement `Trexon.Web.Hosting.ITrexonStartup.InitializeAsync(CancellationToken)` and
register it with `app.AddStartup<T>()`. Constructor dependencies are injected normally.
After all module initializers, handlers run in registration order, once per type,
with a separate async DI scope for each handler. Initialization and scope disposal
finish before BuildAsync returns and before listening. Cancellation propagates;
a failure names the startup handler and disposes the host. Do not retain scoped
services after initialization. ITrexonModule remains compatible and is still
constructed before DI; use Configure for registrations and startup handlers for I/O.

The generated NotesModule/SchemaStartup and the full sample's AdminSeed demonstrate
real database/account initialization.

## Startup diagnostics

The default container validates service graphs and singleton/scoped lifetimes in
all environments. Registered route and socket types participate. Required explicit
[FromServices] parameters, named authorization policies and route conflicts are
checked before accepting requests. Missing registrations identify the route/service
and suggest app.Services. Factories and IServiceProvider lookups are checked when
actually resolved; request constructors are not invoked artificially at startup.
Use AddOptions<T>().ValidateDataAnnotations().ValidateOnStart() for required settings.
A replacement DI container needs its own graph/lifetime validation configuration.

## A scope per WebSocket message

Derive from `SocketMessageHandler<T>` or `SocketMessageHandler<TIn,TOut>` and register
with the existing `AddSocket<T>()`. Every parsed message gets a fresh async scope,
shared by its validators and handler, disposed after processing including failures.
Messages within one connection are sequential. Connection ID, authenticated identity,
rooms, transport and limits persist. Do not retain scoped services for background use.

Message handlers have no custom connected/disconnected callbacks. Use the existing
SocketHandler for connection state and lifecycle callbacks; its behavior is unchanged.
The full sample's authenticated /ws/tasks endpoint shows a message-scoped handler.

## Project options

Run `dotnet new trexon-api -n Orders.Api --docker --tests`. Both flags are optional
and independent. `dotnet test tests/Orders.Api.Tests.csproj` exercises the shared
NotesModule with a temporary SQLite database. The main project excludes test sources
and test content from publication.

`docker compose up --build` builds the generated Dockerfile, listens on 0.0.0.0:8080
and publishes port 5080. Open http://localhost:5080/docs or /docs/ws/. The container
runs as a non-root user; named volumes retain SQLite and logs across container
recreation. Removing volumes deletes their contents. Local APP_HOST defaults to
127.0.0.1. Rebuild the image after source changes. The starter notes collection is
public; add authentication for your application's access requirements.
