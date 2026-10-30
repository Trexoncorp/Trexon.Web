# Trexon.Web

[English](https://github.com/Trexoncorp/Trexon.Web/blob/main/README.md) · [Русский](https://github.com/Trexoncorp/Trexon.Web/blob/main/README.ru.md) · [Deutsch](https://github.com/Trexoncorp/Trexon.Web/blob/main/README.de.md)

HTTP-APIs mit Attributen für C# / .NET 8+: explizite Routenklassen, asynchroner Start,
automatische Validierung, Swagger UI unter `/docs` und optionale Module.
Auf ASP.NET Core MVC und Kestrel aufgebaut. MIT-Lizenz.

**Version `1.0.1`.** [NuGet](https://www.nuget.org/packages/Trexon.Web/1.0.1) · [Änderungen](https://github.com/Trexoncorp/Trexon.Web/blob/main/CHANGELOG.md). Ein Geschwindigkeitsvorteil gegenüber Minimal APIs wird nicht behauptet.

## API-Projekt erstellen

Nach der Installation von `Trexon.Templates` erstellt dieser Befehl eine Notizen-API
mit Swagger, SQLite, Validierung, typisierten Einstellungen und strukturierten Logs:

```sh
dotnet new install Trexon.Templates
dotnet new trexon-api -n MyApi
cd MyApi
dotnet run
```

Öffnen Sie [127.0.0.1:5090/docs](http://127.0.0.1:5090/docs).
[Lokales Vorlagenpaket bauen und installieren](templates/README.md).

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

Die [Schritt-für-Schritt-Anleitung](https://github.com/Trexoncorp/Trexon.Web/blob/main/examples/Trexon.Web.Sample/README.md) enthält
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
| Request-Logging über `Trexon.Logging.Log.Info` | Standard; eigener Sink: `UseRequestLogging(sink)` |
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
[WebSocket-Leitfaden](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/WEBSOCKETS.md).

## Einbindung und Prüfung

Das stabile Paket installieren:

```sh
dotnet new web -n Hello
dotnet add Hello/Hello.csproj package Trexon.Web --version 1.0.1
dotnet build Trexon.Web.slnx --configuration Release -warnaserror
dotnet test --solution Trexon.Web.slnx --configuration Release --no-build --no-restore
python3 scripts/check-docs.py
python3 scripts/smoke-sample.py
```

Die ersten beiden Befehle erstellen ein unabhängiges Beispiel; die Prüfungen werden
im Trexon.Web-Repository ausgeführt. [Deutscher Leitfaden](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/de/README.md),
[englischer Leitfaden](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/en/README.md), [API-Referenz](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/API.md),
[Minimal-APIs-Vergleich](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/MINIMAL_APIS.md), [Paket erstellen](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/PUBLISHING.md).

I/O und Host-Lebenszyklus verwenden async APIs mit Abbruchtoken. Konfiguration und
CPU-Arbeit bleiben synchron. Microsoft.Data.Sqlite führt seine async Aufrufe intern
synchron aus. Ein eigener `IUserStore` kann eine echte asynchrone Netzwerkdatenbank nutzen.
Native AOT und Trimming werden nicht unterstützt.

## URL-Präfixe

`app.AddRoutes<GreetingRoutes>("/v1")` setzt ein Präfix für eine Routengruppe;
`AddSocket<T>("/v1")` gilt für WebSockets. `api.Prefix = "/v1"` in `TrexonApp.Create`
verschiebt den gesamten Dienst einschließlich Swagger nach `/v1/docs`, Konsole nach
`/v1/docs/ws` und aller Auth-/Health-Endpunkte.
[Beispiel](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/ROUTING.md#префикс-роутера-и-всего-приложения).
