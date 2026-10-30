# Trexon.Web Leitfaden

[Startseite](../../README.de.md) · [English](../en/README.md) · [Русский](../README.md)

Trexon.Web stellt explizite Routenklassen, Attribute und wiederverwendbare Module
auf ASP.NET Core MVC/Kestrel bereit. Beschrieben wird die stabile Version `1.0.1`.
Ein Leistungsvorteil gegenüber Minimal APIs und Native-AOT-Unterstützung werden nicht behauptet.


Mit `UseUsers()` bietet Swagger **Authorize** die Anmeldung mit Benutzername und Passwort.
Das Bearer-Token wird automatisch verwendet und nur im Speicher des Tabs gehalten.
Das vollständige Beispiel enthält Anwendung und Chat-Seite in einer einzigen `Program.cs`.

## Einstieg

SDK 10 und ASP.NET Core Runtime 8 installieren. Im Repository:

```sh
dotnet run --project examples/Trexon.Web.Sample
```

`http://localhost:5080/docs` öffnet Swagger, `/openapi.json` das Schema. Das vollständige
[Beispiel](../../examples/Trexon.Web.Sample/README.md) enthält SQLite-Aufgaben je Benutzer,
Registrierung, JWT/Refresh, Rollen, Policies, API-Schlüssel, Cookies mit CSRF,
Validierung, Fehler, Cache, CORS, Ratenbegrenzung und Health Checks.

Ohne TREXON_JWT_KEY wird ein Schlüssel für den aktuellen Prozess erzeugt. Mit
TREXON_API_KEY und TREXON_ADMIN_PASSWORD werden die zusätzlichen Zugangsarten aktiviert.
Zufällige Geheimnisse verwenden; das Programm gibt sie nicht aus. TREXON_DATA_DIR wählt
die Datenablage, TREXON_URL den Listener. Logging, Configuration und Data werden aus
den vorhandenen Trexon-NuGet-Paketen eingebunden.

## Eigenes erstes Programm

Mit `dotnet new web -n Hello` eine ASP.NET-Core-Web-App erstellen, dann
`dotnet add Hello/Hello.csproj package Trexon.Web --version 1.0.1` ausführen. Program.cs:

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

`GET /hello/Ada` liefert `{"name":"Ada"}`. Nur mit AddRoutes<T> verbundene Klassen werden
veröffentlicht; eine Basisklasse und Assembly-Scanning sind nicht nötig.

## Routen und Modelle

[Routes] legt das Präfix fest. Methoden verwenden [Get], [Post], [Put], [Patch], [Delete],
[Head] oder [Options]; Standard-MVC-HTTP-Attribute funktionieren ebenfalls. Öffentliche
Hilfsmethoden ohne HTTP-Attribut sind keine Endpoints. Generische/statische Handler und
ref/out-Parameter werden abgewiesen. Constructor Injection nutzt normales scoped DI.

Einfache Parameter kommen aus dem Pfad, wenn ihr Name im Muster steht, sonst aus der
Query. Komplexe Modelle verwenden das API-Controller-Binding, meist JSON im Body.
[FromQuery], [FromBody], [FromHeader], [FromServices] und andere MVC-Attribute machen
die Quelle explizit. CancellationToken entspricht RequestAborted.

Task<T> und ValueTask<T> sind der bevorzugte Handler-Vertrag. Objekte werden JSON,
Strings verwenden das MVC-Textformat. Für Status und Header IActionResult/ActionResult<T>
verwenden. [Response] dokumentiert eine Antwort und ändert nicht den tatsächlichen Status.
[Tag] gruppiert, [Summary] und Description beschreiben Operationen. Offensichtliche
Verb/Pfad-Duplikate werden beim Start erkannt; überlappende Constraints bleiben Sache
des ASP.NET-Routers und sollten durch HTTP-Tests geprüft werden.

## Validierung und Fehler

DataAnnotations validieren Parameter und verschachtelte Modelle automatisch. Fehlerhaftes
JSON, Binding-Fehler und ungültige Modelle liefern 400 mit errors pro Feld.
AddValidator<T,TValidator> verbindet zusätzliche scoped IRequestValidator<T>, deren
ValidateAsync Business-Prüfungen mit CancellationToken ausführen kann. Mehrere Prüfer
laufen nacheinander vor dem Handler; verschachtelte Business-Regeln prüft der Eltern-Validator.

HttpError(status, safeDetail) erzeugt einen beabsichtigten Fehler. Errors(e =>
e.Map<MyException>(404, "Not found")) ordnet eigene Ausnahmen zu, ohne deren Nachricht
weiterzugeben. Unerwartete Ausnahmen werden geloggt und liefern 500 ohne interne Details.
IExceptionResponder kann über DI ersetzt werden. IncludeExceptionDetails ist standardmäßig false.

Leere 404/405/401/403-Antworten verwenden Problem Details. X-Request-ID wird ausgegeben;
traceId nutzt die aktive Activity oder Request-ID. Erlaubte eingehende IDs sind 1–64
ASCII-Buchstaben/Ziffern/Bindestriche/Unterstriche. Bereits gestartete Antworten können
nicht durch einen neuen Fehlerbody ersetzt werden. Request-Abbruch wird nicht als 500 umgedeutet.

## JWT, Rollen und Policies

UseJwt(new JwtSettings(issuer, audience, signingKey)) richtet Signatur-, Issuer-, Audience-
und Ablaufprüfung sowie lokale Ausgabe und Swagger Authorize gemeinsam ein. Zufälliger
Schlüssel mit mindestens 32 UTF-8-Bytes; HS256, 15 Minuten Access-Laufzeit und 30 Sekunden
Clock Skew sind die Defaults. In Swagger nur den Token ohne Bearer-Präfix einfügen.
ITokenIssuer ist für vertrauenswürdigen Servercode über DI verfügbar.

UseJwtAuthority("https://identity.example", audience) prüft Tokens eines externen
OIDC-Ausstellers und verwendet dessen Metadaten/Schlüssel. Lokale Token-Ausgabe ist dann
nicht aktiv. Beide Varianten verwenden Bearer als Schemanamen; pro App eine auswählen.

[Authorize], [Authorize(Roles = "Admin")], [Authorize(Policy = "reports")] und
[AllowAnonymous] sind die Standardattribute von ASP.NET Core. Policies und eigene
AuthorizationHandler werden über Services.AddAuthorization registriert. 401 bedeutet
fehlende/ungültige Anmeldung, 403 fehlende Berechtigung.

UseApiKeys<MyValidator>() verwendet einen scoped IApiKeyValidator, standardmäßig mit
X-API-Key. ValidateAsync liefert einen authentifizierten ClaimsPrincipal oder null.
Speicherung, Ablauf und Widerruf legt die Anwendung fest. AuthenticationSchemes =
AuthSchemes.ApiKey beschränkt eine Operation auf Schlüssel; AuthSchemes.Bearer auf JWT.

Bei mehreren Schemata wählt einfaches Authorize zuerst einen Bearer-Header, dann einen
API-Key-Header, dann Cookies, dann das erste registrierte Schema. Ein ungültiger Bearer
fällt nicht still auf eine Cookie-Sitzung zurück. Swagger beschreibt Alternativen und
explizite Schemanamen; komplexe Policy-Logik benötigt zusätzlich eine Textbeschreibung.

## Lokale Benutzer und Cookies

UseJwt(settings) vor UseUsers("data/accounts.db") aufrufen. Registrierung erwartet
username (3–64 ASCII-Zeichen, Buchstaben/Ziffern/_.-) und password (12–128 Zeichen).
Passwörter werden mit Identity PasswordHasher und 210000 Iterationen gehasht. Vom Client
übergebene Rollen werden ignoriert; Standardrolle ist User.

POST /auth/register liefert UserInfo mit Status 201. POST /auth/login liefert Access-
und Refresh-Token sowie Ablaufdaten und öffentliche Benutzerdaten. GET /auth/me liest
den lokalen Benutzer. POST /auth/refresh ersetzt einen Refresh-Token atomar; POST /auth/logout
widerruft seine Familie. Nur SHA-256-Hashes der zufälligen Refresh-Tokens werden gespeichert.
Wiederverwendung eines alten Tokens widerruft auch seinen aktuellen Nachfolger.
Clients müssen Refresh-Anfragen serialisieren. Die sieben Tage Familienlaufzeit verlängern
sich nicht bei Rotation. Bestehende Access-JWTs und Cookies bleiben bis zu ihrem eigenen
Ablauf gültig, auch nach Refresh-Logout oder Rollenänderung.

UsersOptions ändert Prefix, AllowRegistration, RegistrationRoles und RefreshLifetime.
Ein eigener singleton IUserStore kann statt SQLite registriert werden; UseUsers() dann
ohne Pfad aufrufen. InitializeAsync muss idempotent und Rotation transaktional sein.
Netzwerk-Store, MFA, Passwort-Reset, E-Mail-Bestätigung, Lockout-Zähler und automatische
Bereinigung alter Refresh-Einträge sind nicht enthalten. Das Modul ist kein OIDC-Server.

UseCookies() erstellt Secure/HTTP-only/SameSite=Lax-Cookies mit acht Stunden Sliding Expiration
und API-401/403 statt HTML-Redirects. Mit UsersModule gilt über HTTPS:

1. GET /auth/cookie/csrf setzt das Antiforgery-Cookie und liefert requestToken.
2. POST /auth/cookie/login erhält Zugangsdaten, Cookie und X-CSRF-Token.
3. Nach Login einen neuen, an die Identität gebundenen CSRF-Token abrufen.
4. Diesen bei Cookie-POST/PUT/PATCH/DELETE und POST /auth/cookie/logout senden.

Swagger sendet ein bestehendes gleichnamiges Origin-Cookie automatisch, kann aber ein
HTTP-only-Cookie nicht per Authorize setzen. Automatische CSRF-Injektion in Swagger UI
ist nicht enthalten. Bearer/API-Key ohne Cookie-Identität benötigt keinen CSRF-Token.
Eigene Cookie-Login-Handler müssen Antiforgery vor SignInAsync prüfen.

## Module, Async und Betrieb

ITrexonModule.Configure registriert DI und Routen, Use baut Middleware auf,
InitializeAsync führt erwartete Startup-I/O vor dem Listener aus. Die ersten beiden
Schritte sind synchrone Konfiguration ohne I/O. Initialisierung erfolgt nacheinander.
Doppelte Modultypen werden abgewiesen. AddModule<T>() oder AddModule(instance) verbindet sie.

UseCors(origin), UseRateLimit(100), UseOutputCache() und UseHealthChecks() sind fertige Module.
Jeder abgeschlossene Request wird standardmäßig über `Trexon.Logging.Log.Info` mit der
Konfiguration aus `Log.Configure` protokolliert. `UseRequestLogging(sink)` ersetzt den Standard-Sink. Der Limiter verwendet Benutzer-ID oder Remote-IP,
Fixed Window und keine Warteschlange. Cache und Limits gelten pro Prozess. OutputCache
speichert standardmäßig keine authentifizierten Antworten. Health Checks zeigen keine
internen Exception-Texte. Request-Logs enthalten keine Bodies, Queries oder Credentials;
ein asynchroner eigener Sink wird vor Ende der Pipeline erwartet.

RunAsync(token) besitzt und entsorgt den Host. BuildAsync(token) gibt einen initialisierten
WebApplication an den Aufrufer zurück; await using übernimmt die Lebensdauer. Fehler
beim Startup entsorgen bereits erzeugte Services. Es gibt kein sync-over-async und keinen
Background-Job-Scheduler. CPU-Arbeit und Konfiguration bleiben synchron. Microsoft.Data.Sqlite
führt seine async-Methoden intern synchron aus; für echte asynchrone Datenbank-I/O ist
ein geeigneter Netzwerk-Treiber hinter IUserStore erforderlich.

Builder, Services und Configuration bleiben zugänglich. Use ergänzt Middleware nach
Security, Map kann Minimal API Endpoints hinzufügen. MVC-Validierung und eigene Validatoren
gelten für Routenklassen, nicht automatisch für beliebige Minimal API Handler.

[Entwicklung und Prüfungen](../DEVELOPMENT.md), [API-Referenz](../API.md),
[Architektur](../ARCHITECTURE.md), [Betrieb](../OPERATIONS.md),
[Minimal-APIs-Vergleich](../MINIMAL_APIS.md), [Grenzen](../ROADMAP.md) beschreiben die Details.
TLS/Proxy-Vertrauen, gemeinsame Data-Protection-Schlüssel, stabile Secrets und SQLite-Backups
müssen zur konkreten Bereitstellung passen. CI baut, testet .NET 8/10, prüft Dokumentation
und Beispiel und erstellt Pakete; Veröffentlichung ist ein separater Schritt.

## WebSockets mit hoher Abstraktion

AddSocket<ChatSocket>() verbindet den [Socket("/ws/chat/{room}")]-Handler und aktiviert
den Transport automatisch. SocketHandler<T> oder SocketHandler<TIn,TOut> benötigt nur
OnMessageAsync; OnConnectedAsync/OnDisconnectedAsync sind optional. Strings sind Text,
andere Typen JSON. Die Bibliothek übernimmt Fragmente, Validierung, Cancellation,
serialisierte Send-Vorgänge, Limits und den Closing Handshake.

SendAsync antwortet, Join/Leave verwaltet Gruppen, BroadcastAsync sendet an alle oder
an eine Gruppe einschließlich Absender. Endpoints haben getrennte Verzeichnisse.
Handler und scoped Services leben für die Verbindung. DataAnnotations und bestehende
AddValidator-Registrierungen prüfen jede Nachricht; Fehler werden als reservierter JSON-
Umschlag gesendet. Unerwartete Fehler schließen mit 1011 ohne interne Details.

Authorize, Rollen und Policies werden vor dem Upgrade geprüft. Native Clients können
Header senden, Browser verwenden HTTPS-Cookies vom selben Origin. Origin wird unabhängig
von CORS geprüft: gleicher Origin ist erlaubt, weitere werden explizit konfiguriert.
Fehlender Origin ist für native Clients standardmäßig erlaubt. JWT aus Query-Parametern
wird nicht automatisch übernommen. Berechtigungen werden nicht nach jeder Nachricht neu geprüft.

UseWebSockets ändert Defaults: 64 KiB je Nachricht, 120 Nachrichten pro Minute/Verbindung,
zwei Minuten Keep-Alive, zehn Sekunden Send-Timeout, fünf Sekunden Close-Timeout.
Kein verteilter Backplane, dauerhafter Nachrichtenspeicher oder automatischer Reconnect.
Swagger zeigt Verträge im Abschnitt WebSockets und in x-trexon-websockets; HTTP Try it out
ist kein Socket-Client. `/chat` demonstriert öffentliche/private Räume und Cookie-Login.
[Ausführliche Beispiele und Vertrag](../WEBSOCKETS.md).

Die integrierte **WebSocket console** unter `/docs/ws` bietet Routenauswahl, Parameter,
JSON-Beispiele aus Schemas und ein Sende-/Empfangsprotokoll. Die Oberfläche ist Englisch.
Benutzername/Passwort, Bearer-Token und API-Schlüssel werden über kurzlebige, einmalige
Verbindungstickets unterstützt. Schema-, Rollen- und Policy-Prüfungen bleiben erhalten.
Cookie-Anmeldung ist zusätzlich mit `UseUsers()` + `UseCookies()` über HTTPS verfügbar.
`DocsPath` und PathBase werden berücksichtigt; `EnableDocs = false` deaktiviert die Konsole.

Das kleine [Rechner-Beispiel](../../examples/Trexon.Web.Calculator/README.md) enthält die
gesamte API in einer Program.cs. Start: `dotnet run --project examples/Trexon.Web.Calculator`.
`await web.RunAsync(5081)` funktioniert mit TrexonApp und einer gebauten WebApplication
und startet HTTP auf allen IPv4-Schnittstellen (`0.0.0.0:5081`), auch für Docker/Compose. Ohne Port bleibt die Host-Konfiguration einschließlich
HTTPS erhalten. Port 0 wählt einen freien Port.

## URL-Präfixe

`AddRoutes<T>("/v1")` und `AddSocket<T>("/v1")` setzen ein Präfix pro Gruppe. Ein
weiterer Aufruf mit Präfix ersetzt es; ein Aufruf ohne Argument behält es bei.
`api.Prefix = "/v1"` verschiebt die ganze Anwendung einschließlich Swagger nach
`/v1/docs`, WebSocket-Konsole nach `/v1/docs/ws`, Auth und Health.
[Beispiel und Regeln](../ROUTING.md#префикс-роутера-и-всего-приложения).
