# Trexon.Web

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md)

HTTP-APIs mit Attributen für C# / .NET 8+: explizite Routenklassen, asynchroner Start,
automatische Validierung, Swagger UI unter `/docs` und optionale Module.
Auf ASP.NET Core MVC und Kestrel aufgebaut. MIT-Lizenz.

**Version `1.0.0-dev`, noch nicht veröffentlicht.** Die API kann sich vor der ersten
stabilen Version ändern. Ein Geschwindigkeitsvorteil gegenüber Minimal APIs wird nicht behauptet.

## Vollständiges erstes Beispiel

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

`GET /hello/Ada` liefert `{"name":"Ada"}`. `/docs` zeigt die interaktive Dokumentation,
`/openapi.json` das Schema. Keine Controller-Basisklasse und kein Assembly-Scanning nötig.

## Komplettes Beispiel starten

SDK 10 und ASP.NET Core Runtime 8 werden benötigt:

```sh
dotnet run --project examples/Trexon.Web.Sample
```

[localhost:5080/docs](http://localhost:5080/docs) öffnen. Das Beispiel enthält SQLite-CRUD
mit Eigentümerprüfung, Registrierung, JWT und Refresh-Rotation, Rollen, Policies,
API-Schlüssel, sichere Cookie-Sitzungen mit CSRF-Schutz über HTTPS, eigene Validierung,
Fehlerzuordnung, Module, Caching, CORS, Ratenbegrenzung und Health Checks.
Trexon.Logging, Trexon.Configuration und Trexon.Data sind eingebunden.

Die [Schritt-für-Schritt-Anleitung](examples/Trexon.Web.Sample/README.md) enthält
Aufrufe für jede Funktion. Ohne konfigurierten Schlüssel erzeugt das Beispiel einen
JWT-Schlüssel nur für den aktuellen Prozess.

## Module

| Funktion | Aufruf |
| --- | --- |
| Routing, DataAnnotations, Fehlerantworten, OpenAPI | Standardmäßig |
| JWT und Swagger Authorize | `UseJwt(settings)` |
| Externer JWT-Aussteller | `UseJwtAuthority(authority, audience)` |
| API-Schlüssel | `UseApiKeys<TValidator>()` |
| Cookies und CSRF | `UseCookies()` |
| Lokale Benutzer und Refresh-Tokens | `UseUsers(path)` |
| Rollen und Policies | `[Authorize]` |
| CORS | `UseCors(origin)` |
| Ratenbegrenzung | `UseRateLimit(100)` |
| Öffentlicher Antwort-Cache | `UseOutputCache()` |
| Health Checks | `UseHealthChecks()` |
| Request-Logging | `UseRequestLogging()` |
| Typisierte WebSockets und Räume | `AddSocket<THandler>()` |
| Eigene Module | `AddModule<T>()` |

Ein Aufruf richtet JWT-Prüfung und Swagger gemeinsam ein. Aussteller, Audience und
Signaturschlüssel müssen aus vertrauenswürdiger Konfiguration kommen.

## WebSocket ohne manuelle Empfangsschleife

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

Andere Nachrichtentypen verwenden automatisch JSON; `SocketHandler<TIn,TOut>` trennt
Ein- und Ausgabe. Bestehende Authorize-Attribute und Validatoren gelten auch für Sockets.
Im Beispiel zeigt `/chat` öffentliche und Cookie-geschützte Räume.
[WebSocket-Leitfaden](docs/WEBSOCKETS.md).

## Einbindung und Prüfung

Noch kein veröffentlichtes Paket; zunächst eine Projektreferenz verwenden:

```sh
dotnet new web -n Hello
dotnet add Hello/Hello.csproj reference /path/to/Trexon.Web/Trexon.Web/Trexon.Web.csproj
dotnet build Trexon.Web.slnx --configuration Release -warnaserror
dotnet test --solution Trexon.Web.slnx --configuration Release --no-build --no-restore
python3 scripts/check-docs.py
python3 scripts/smoke-sample.py
```

Die ersten beiden Befehle erstellen ein unabhängiges Beispiel; die Prüfungen werden
im Trexon.Web-Repository ausgeführt. [Deutscher Leitfaden](docs/de/README.md),
[englischer Leitfaden](docs/en/README.md), [API-Referenz](docs/API.md),
[Minimal-APIs-Vergleich](docs/MINIMAL_APIS.md), [Paket erstellen](docs/PUBLISHING.md).

I/O und Host-Lebenszyklus verwenden async APIs mit Abbruchtoken. Konfiguration und
CPU-Arbeit bleiben synchron. Microsoft.Data.Sqlite führt seine async Aufrufe intern
synchron aus. Ein eigener `IUserStore` kann eine echte asynchrone Netzwerkdatenbank nutzen.
Native AOT und Trimming werden nicht unterstützt.
