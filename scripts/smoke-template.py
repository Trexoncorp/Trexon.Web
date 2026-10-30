#!/usr/bin/env python3
"""Pack/install/generate a real consumer; check HTTP and restart persistence."""
import argparse
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "templates/Trexon.Templates.csproj"


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def run(*args, cwd, env):
    result = subprocess.run(args, cwd=cwd, env=env, capture_output=True,
                            text=True, encoding="utf-8", errors="replace", timeout=180)
    if result.returncode:
        raise RuntimeError(f"Command failed: {args}\n{result.stdout}\n{result.stderr}")
    return result.stdout


class Server:
    def __init__(self, project, env):
        self.project = project
        self.env = dict(env)

    def __enter__(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        self.base = f"http://127.0.0.1:{port}"
        self.env["APP_PORT"] = str(port)
        self.output = open(self.project / "server.log", "a+", encoding="utf-8")
        self.process = subprocess.Popen(
            ["dotnet", "run", "-c", "Release", "--no-build", "--no-launch-profile"],
            cwd=self.project, env=self.env, stdout=self.output, stderr=subprocess.STDOUT,
            start_new_session=os.name != "nt")
        try:
            deadline = time.monotonic() + 35
            while time.monotonic() < deadline:
                if self.process.poll() is not None:
                    raise RuntimeError("Generated application exited before becoming ready")
                try:
                    health, _ = self.call("GET", "/health")
                    check(health["checks"]["notes-sqlite"]["status"] == "Healthy", "Database not ready")
                    return self
                except (OSError, urllib.error.URLError):
                    time.sleep(0.1)
            raise TimeoutError("Generated application did not start")
        except BaseException:
            self.__exit__(None, None, None)
            raise

    def __exit__(self, *_):
        if self.process.poll() is None:
            if os.name == "nt":
                # dotnet run owns a child process on Windows; close the whole tree.
                subprocess.run(["taskkill", "/PID", str(self.process.pid), "/T", "/F"],
                               capture_output=True, timeout=15)
            else:
                os.killpg(self.process.pid, signal.SIGINT)
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                if os.name == "nt":
                    self.process.kill()
                else:
                    os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=5)
        self.output.close()

    def call(self, method, path, body=None, expected=200, headers=None):
        values = dict(headers or {})
        payload = None
        if body is not None:
            payload = json.dumps(body).encode("utf-8")
            values["Content-Type"] = "application/json"
        request = urllib.request.Request(self.base + path, data=payload, method=method, headers=values)
        try:
            response = urllib.request.urlopen(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            content = response.read().decode("utf-8")
            check(response.status == expected, f"{method} {path}: {response.status}, expected {expected}: {content}")
            parsed = json.loads(content) if content and "json" in response.headers.get("Content-Type", "") else content
            return parsed, response.headers


def verify_app(project, env):
    # A bad .env port plus a successful env override checks source precedence.
    (project / ".env").write_text('APP_NAME="Template smoke"\nAPP_PORT=70000\n', encoding="utf-8")
    try:
        with Server(project, env) as server:
            document, _ = server.call("GET", "/openapi.json")
            check(document["info"]["title"] == "Template smoke", ".env was not loaded")
            check({"/notes", "/notes/{id}"} <= document["paths"].keys(), "Missing CRUD in OpenAPI")
            page, _ = server.call("GET", "/")
            check("swagger-ui" in page, "Root does not open Swagger")
            notes, _ = server.call("GET", "/notes")
            check(notes == [], "New database is not empty")
            for invalid in ({"title": " a "}, {"title": None}, {"title": "Valid", "text": None}):
                error, _ = server.call("POST", "/notes", invalid, expected=400)
                check(error["errors"] and error["traceId"], "Missing validation Problem Details")
            server.call("GET", "/notes?take=101", expected=400)
            note, headers = server.call("POST", "/notes", {"title": "  First note  ", "text": "Persistent"},
                                        expected=201, headers={"X-Request-ID": "template-smoke"})
            note_id = note["id"]
            check(note["title"] == "First note", "Title was not trimmed")
            check(headers["Location"] == f"/notes/{note_id}", "Wrong Location")
            check(headers["X-Request-ID"] == "template-smoke", "Missing request ID")
            saved, _ = server.call("GET", f"/notes/{note_id}")
            check(saved == note, "Note not saved")
            note["archived"] = True
            updated, _ = server.call("PUT", f"/notes/{note_id}", note)
            check(updated == note, "Update not saved")
            archived, _ = server.call("GET", "/notes?archived=true")
            check(archived == [note], "Filter failed")

        with Server(project, env) as server:
            saved, _ = server.call("GET", f"/notes/{note_id}")
            check(saved == note, "Data lost after restart")
            server.call("DELETE", f"/notes/{note_id}", expected=204)
            missing, _ = server.call("GET", f"/notes/{note_id}", expected=404)
            check(missing["status"] == 404 and missing["traceId"], "Missing 404 Problem Details")

        events = [json.loads(line) for line in (project / "logs/app.jsonl").read_text(encoding="utf-8").splitlines()]
        check(any(event["properties"].get("RequestId") == "template-smoke" for event in events), "No business log")
        check(any(event["properties"].get("TraceId") == "template-smoke" and
                  event["properties"].get("StatusCode") == 201 for event in events), "No correlated HTTP log")
        changes = [event["properties"]["Changed"] for event in events if "Schema ready" in event["message"]]
        check(changes == [True, False], "Schema was not idempotent")

        invalid = subprocess.run(["dotnet", "run", "-c", "Release", "--no-build", "--no-launch-profile"],
                                 cwd=project, env=env, capture_output=True, text=True, timeout=20)
        check(invalid.returncode == 1 and "Port" in invalid.stderr, "Invalid settings accepted")
    except BaseException:
        log = project / "server.log"
        if log.exists():
            print(log.read_text(encoding="utf-8", errors="replace")[-12000:])
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--package", type=Path, help="Verify an already packed template nupkg")
    source.add_argument("--public", action="store_true", help="Install Trexon.Templates from NuGet.org in a clean environment")
    parser.add_argument("--sdk", help="SDK version for template consumers (latest feature band allowed)")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="trexon-template-") as temporary:
        directory = Path(temporary)
        env = {key: value for key, value in os.environ.items() if not key.startswith("APP_")}
        env.update(DOTNET_CLI_HOME=str(directory / "cli"), DOTNET_NOLOGO="1",
                   DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
        if args.public:
            env.update(NUGET_PACKAGES=str(directory / "nuget-packages"),
                       NUGET_HTTP_CACHE_PATH=str(directory / "nuget-http-cache"))
            (directory / "NuGet.Config").write_text(
                '<configuration><packageSources><clear />'
                '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
                '</packageSources></configuration>', encoding="utf-8")
            version = ET.parse(PROJECT).findtext("PropertyGroup/Version")
            url = f"https://api.nuget.org/v3-flatcontainer/trexon.templates/{version}/trexon.templates.{version}.nupkg"
            package = directory / f"Trexon.Templates.{version}.nupkg"
            with urllib.request.urlopen(url, timeout=30) as response:
                package.write_bytes(response.read())
        elif args.package:
            package = args.package.resolve(strict=True)
        else:
            run("dotnet", "pack", str(PROJECT), "-c", "Release", "-o", str(directory / "packages"),
                "-warnaserror", cwd=ROOT, env=env)
            version = ET.parse(PROJECT).findtext("PropertyGroup/Version")
            package = directory / f"packages/Trexon.Templates.{version}.nupkg"
        with zipfile.ZipFile(package) as archive:
            files = {name.removeprefix("content/trexon-api/") for name in archive.namelist() if name.startswith("content/")}
            expected = {".template.config/template.json", ".template.config/dotnetcli.host.json",
                        ".env.example", ".gitignore", "README.md",
                        "TrexonApi.csproj", "TrexonApi.http", "Program.cs", "AppSettings.cs",
                        "Note.cs", "NotesRoutes.cs", "NotesHealthCheck.cs"}
            check(files == expected, f"Unexpected template payload: {files ^ expected}")
        if args.sdk:
            (directory / "global.json").write_text(json.dumps({
                "sdk": {"version": args.sdk, "rollForward": "latestFeature", "allowPrerelease": False}
            }), encoding="utf-8")
        run("dotnet", "new", "install", "Trexon.Templates" if args.public else str(package),
            cwd=directory, env=env)
        print("Template package installed in isolated CLI home", flush=True)
        try:
            # Normal user path: preferNameDirectory + restore post-action.
            run("dotnet", "new", "trexon-api", "-n", "Orders.Api", cwd=directory, env=env)
            project = directory / "Orders.Api"
            check((project / "obj/project.assets.json").exists(), "Restore post-action did not run")
            check((project / ".env.example").exists() and (project / ".gitignore").exists(), "Missing dotfiles")
            check(not (project / ".template.config").exists(), "Template metadata leaked into project")
            check(not list(project.rglob("*.slnx")), "Generated app should not require SDK 10 solution support")
            check((project / "Orders.Api.http").exists(), "HTTP file not renamed")
            for file in project.glob("*.cs"):
                check("TrexonApi" not in file.read_text(encoding="utf-8"), f"Unreplaced source name in {file}")
            run("dotnet", "build", "-c", "Release", "-warnaserror", cwd=project, env=env)
            print("Generated project restored and built; checking HTTP and restart", flush=True)
            verify_app(project, env)

            # Punctuation must become a valid namespace; output paths can contain spaces.
            named = directory / "custom output"
            run("dotnet", "new", "trexon-api", "-n", "Shop-Api", "-o", str(named), "--no-restore",
                cwd=directory, env=env)
            check(not (named / "obj/project.assets.json").exists(), "--no-restore was ignored")
            run("dotnet", "build", "-c", "Release", "-warnaserror", cwd=named, env=env)

            # The shortest command uses the current directory as the project name.
            bare = directory / "BareApi"
            bare.mkdir()
            run("dotnet", "new", "trexon-api", "--no-restore", cwd=bare, env=env)
            check((bare / "BareApi.csproj").exists(), "Default project name did not use current directory")
            run("dotnet", "build", "-c", "Release", "-warnaserror", cwd=bare, env=env)
        finally:
            run("dotnet", "new", "uninstall", "Trexon.Templates", cwd=directory, env=env)
    print("OK: template package, isolated install, naming, restore, build, Swagger, CRUD, settings, logs and restart persistence")


if __name__ == "__main__":
    main()
