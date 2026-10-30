# Trexon.Web.Testing

In-process HTTP and WebSocket tests for Trexon.Web. No TCP port, external server,
or authentication bypass. Targets .NET 8 and .NET 10 with the matching TestHost.

```csharp
using Trexon.Web.Testing;

await using var api = await TrexonTest.CreateAsync(app => app.AddRoutes<CalculatorRoutes>());
var response = await api.Client.GetAsync("/calculator/add?a=2&b=3");
response.EnsureSuccessStatusCode();
```

`CalculatorRoutes` is your application route class. Reuse your normal registration
method as the configure delegate. Optional `overrides` runs afterwards; use
`services.Replace(...)` from Microsoft.Extensions.DependencyInjection.Extensions
to replace a production service with a fake before startup handlers run.
`CreateWebSocketClient()` exercises the same middleware and authorization.
Pass cancellation to CreateAsync and requests. Dispose connected sockets before
disposing the test application. Use only in test projects.

[Complete guide](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/en/DEVELOPER_EXPERIENCE.md).
