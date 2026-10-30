# Trexon.Templates

`dotnet new trexon-api` creates a small notes API using the published Trexon packages:
Swagger, validated CRUD, SQLite, typed environment settings and structured logs.

## Install

```sh
dotnet new install Trexon.Templates
dotnet new trexon-api -n MyApi
cd MyApi
dotnet run
```

Open [127.0.0.1:5090/docs](http://127.0.0.1:5090/docs).
Install .NET SDK 8 or newer and ASP.NET Core Runtime 8 (included in SDK 8).
The generated project uses published NuGet packages and needs no repository checkout.

## Build and install a local package

From the Trexon.Web repository, using SDK 10:

```sh
dotnet pack templates/Trexon.Templates.csproj -c Release -o artifacts/templates -warnaserror
dotnet new install ./artifacts/templates/Trexon.Templates.1.0.0.nupkg
dotnet new trexon-api -n MyApi
cd MyApi
dotnet run
```

Open [127.0.0.1:5090/docs](http://127.0.0.1:5090/docs).
The generated application targets .NET 8 and needs ASP.NET Core Runtime 8;
it can be built with SDK 8 or newer. No repository checkout is needed after generation.
Use `-o path/to/project` to choose the output directory or `--no-restore` to defer restore.

Uninstall with `dotnet new uninstall Trexon.Templates`.

## Verify

```sh
python3 scripts/smoke-template.py
```

The check packs the template, installs it into an isolated CLI home, creates projects
with renamed namespaces, builds them and checks real HTTP, CRUD, configuration,
logs and SQLite persistence across restart. Temporary processes and files are cleaned up.
CI runs this check on Linux, Windows and macOS and retains the template nupkg.
CI also selects SDK 8 for generated consumers with `--sdk 8.0.100` to verify the
minimum supported SDK; the packaging project uses the repository's SDK 10.

## Distribution

This is a separate package from Trexon.Web, with its own version in
`Trexon.Templates.csproj`. The public package is
[Trexon.Templates on NuGet.org](https://www.nuget.org/packages/Trexon.Templates).
Share a verified nupkg directly or install from NuGet.org:

```sh
dotnet new install Trexon.Templates
```

Building the package does not publish it. The Web release workflow continues to
release only Trexon.Web; template publishing is a separate operation.

Before publishing, pack from a committed source revision, run the smoke check and
publish that exact nupkg through NuGet.org Upload. Keep the package and its SHA256
checksum in a GitHub release tagged `templates-vVERSION`; do not move published tags.
After NuGet validation completes, verify the actual public install:

```sh
python3 scripts/smoke-template.py --public --sdk 8.0.100
```
