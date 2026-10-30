# Entwicklung und Tests in 1.1

[Handbuch](README.md) · [Englische Anleitung](../en/DEVELOPER_EXPERIENCE.md) ·
[Vollständige Beispiele (Russisch)](../DEVELOPER_EXPERIENCE.md)

## Tests ohne TCP-Port

Das separate Paket `Trexon.Web.Testing` 1.1.0 gehört ins Testprojekt. Es enthält
TestHost passend zu .NET 8 und .NET 10; Produktionsdienste benötigen es nicht.
`await TrexonTest.CreateAsync(ConfigureApi)` verwendet dieselbe Action<TrexonApp>
wie die produktive Registrierung. `api.Client` ist ein HttpClient mit Basisadresse
http://localhost, `api.CreateWebSocketClient()` erreicht dieselbe Anwendung.
Middleware, Validierung und Authentifizierung bleiben aktiv. Tokens, Cookies und
API-Schlüssel werden regulär geprüft. Für Cookie-Tests die HTTPS-Basisadresse setzen;
TestServer führt keinen TLS-Handshake aus.

Der optionale Parameter `overrides` läuft nach der Registrierung und vor Build/Startup.
Mit `services.Replace(...)` lassen sich Abhängigkeiten durch Testimplementierungen
ersetzen. `api.Services` ist der Root-Provider: scoped Dienste über CreateAsyncScope
auflösen. Übergebene Instanzen bleiben in Verantwortung des Aufrufers. Anwendung mit
`await using` entsorgen; WebSockets vorher schließen. Abbruchtoken an CreateAsync und
Clientoperationen weitergeben. Bei Startfehlern wird der erstellte Host entsorgt.

## Aktueller Benutzer

`Trexon.Web.Security.ICurrentUser` ist automatisch scoped registriert. Konstruktoren
von Routen, Diensten und WebSocket-Handlern können ihn anfordern. IsAuthenticated
prüft authentifizierte Identities. Id liest NameIdentifier oder sub; ohne gültige,
nichtleere Benutzer-ID entsteht HTTP 401. Name ist optional, Roles/IsInRole beachten
den jeweiligen RoleClaimType. Principal stellt den zugrunde liegenden ClaimsPrincipal
bereit. Anonyme Claims verleihen weder ID noch Rollen. [Authorize], Rollen und Policies
kontrollieren weiterhin den Zugriff. Außerhalb einer Anfrage ist der Benutzer anonym;
bei WebSockets bleibt die Identität des Handshakes erhalten.

## Asynchroner Startup mit scoped DI

`ITrexonStartup` aus Trexon.Web.Hosting implementieren und mit AddStartup<T>() registrieren.
InitializeAsync erhält das Abbruchtoken, weitere Abhängigkeiten kommen über den Konstruktor.
Nach den Modulinitialisierungen laufen die Handler nacheinander in Registrierungsreihenfolge.
Jeder Handler hat einen eigenen Async-Scope. Initialisierung und DisposeAsync enden vor
BuildAsync-Rückgabe und Listenerstart. Ein Typ wird nur einmal registriert. Fehler nennen
den Handler und entsorgen den Host; Abbrüche bleiben Abbrüche. Scoped Dienste danach nicht
weiterverwenden. Die bisherigen ITrexonModule-Verträge bleiben erhalten; Configure registriert
Dienste vor dem Containeraufbau. SchemaStartup des Templates und AdminSeed des Beispiels
zeigen echte Datenbank- und Kontoinitialisierung.

## Fehler früh erkennen

Der Standardcontainer prüft DI-Graphen und Lifetimes in allen Umgebungen, einschließlich
registrierter HTTP- und WebSocket-Handler. Fehlende erforderliche [FromServices]-Parameter,
unbekannte benannte Policies und Routenkonflikte werden vor der Annahme von Anfragen erkannt.
Fehlermeldungen verweisen auf den Dienst/die Route und app.Services. Factories und dynamische
IServiceProvider-Aufrufe werden erst beim tatsächlichen Auflösen geprüft. Request-Konstruktoren
werden nicht künstlich ausgeführt. Pflichtkonfiguration über die üblichen AddOptions<T>(),
ValidateDataAnnotations() und ValidateOnStart() prüfen. Ein anderer DI-Container benötigt
seine eigene Validierungskonfiguration.

## Scope pro WebSocket-Nachricht

SocketMessageHandler<T> beziehungsweise SocketMessageHandler<TIn,TOut> mit AddSocket<T>()
registrieren. Jede geparste Nachricht erhält einen neuen Async-Scope für Validatoren und
Handler. Dieser wird nach der Verarbeitung auch bei Fehlern entsorgt. Nachrichten einer
Verbindung laufen nacheinander; Identität, Verbindungs-ID, Räume und Transport bleiben erhalten.
Scoped Dienste nicht für Hintergrundarbeit aufbewahren. Diese Handler haben keine eigenen
Connected/Disconnected-Callbacks. Für Verbindungszustand und Lifecycle weiterhin den
unveränderten SocketHandler verwenden. Das vollständige Beispiel bietet /ws/tasks dafür an.

## Docker und generierte Tests

`dotnet new trexon-api -n Orders.Api --docker --tests` erzeugt die gewünschten Dateien.
Beide Optionen sind unabhängig und standardmäßig aus. Mit
`dotnet test tests/Orders.Api.Tests.csproj` wird das gemeinsame NotesModule mit einer
isolierten temporären SQLite-Datenbank getestet. Testquellen gelangen nicht ins Hauptprojekt.

`docker compose up --build` baut das Dockerfile und veröffentlicht Port 5080; der Dienst
lauscht intern auf 0.0.0.0:8080. Swagger: http://localhost:5080/docs, WebSocket-Konsole:
/docs/ws/. Der Container läuft ohne Root-Rechte. Named Volumes bewahren Datenbank und Logs
beim Neuerstellen des Containers; das Löschen der Volumes löscht diese Daten. Lokal ist
APP_HOST standardmäßig 127.0.0.1. Nach Quelländerungen das Image neu bauen. Die Notes-API
ist zunächst öffentlich; Authentifizierung entsprechend den Anforderungen ergänzen.
