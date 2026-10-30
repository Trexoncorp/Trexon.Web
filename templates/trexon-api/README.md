# TrexonApi

A small API built with Trexon.Web, Configuration, Logging and Data.

## Run

Install .NET SDK 8 or newer **and ASP.NET Core Runtime 8** (included in SDK 8).
From this project directory:

```sh
dotnet run
```

Open [Swagger UI](http://127.0.0.1:5090/docs). The root URL redirects there too.
No database server, secrets or `.env` file are needed for the first run.
The starter listens on loopback and uses a shared notes collection without authentication.

## Try it

Create a note using `POST /notes` in Swagger:

```json
{ "title": "My first note", "text": "Built with Trexon" }
```

Use the returned `id` to read, update or delete it. `GET /notes` lists notes;
`GET /health` checks access to the notes table. `TrexonApi.http` contains sample requests.
Invalid input returns `400`; missing notes return `404` with Problem Details.

The application creates `data/notes.db` and `logs/app.jsonl`. Notes survive restarts.
HTTP and business events are correlated with `X-Request-ID`.
Run from the project directory: configuration, database and log paths use the current working directory.

## Configure

Copy `.env.example` to `.env`, edit it and restart. All settings are optional.
Environment variables override `.env`; property defaults apply last.

| Variable | Default |
| --- | --- |
| `APP_NAME` | `TrexonApi` |
| `APP_PORT` | `5090` |
| `APP_DATA_DIRECTORY` | `data` |
| `APP_LOG_DIRECTORY` | `logs` |
| `APP_LOG_LEVEL` | `Information` |

In PowerShell: `Copy-Item .env.example .env`. In a POSIX shell: `cp .env.example .env`.
To run another generated API alongside this one, set a different `APP_PORT`.

## Extend

- `Program.cs`: composition, dependency injection and schema initialization.
- `AppSettings.cs`: typed settings and startup validation.
- `Note.cs`: entity and validated input model.
- `NotesRoutes.cs`: CRUD handlers; each request gets a separate `DataSession`.
- `NotesHealthCheck.cs`: database readiness check.

Add route classes explicitly with `app.AddRoutes<YourRoutes>()`.
Call `SaveAsync()` for tracked changes; disposing a session does not save them.
`UpdateAsync()` supports a limited set of schema changes; read the
[Data schema guide](https://github.com/Trexoncorp/Trexon.Data/blob/main/docs/SCHEMA.md)
before changing an existing database.

For hosting, HTTPS, authentication and persistent storage, see
[Trexon.Web operations](https://github.com/Trexoncorp/Trexon.Web/blob/main/docs/OPERATIONS.md).
