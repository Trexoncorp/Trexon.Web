#!/usr/bin/env python3
"""Install the actual NuGet package into a clean app, then exercise its HTTP/resources/logs."""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', help='Optional local package feed; dependencies still come from NuGet.org')
    parser.add_argument('--version', default=ET.parse(ROOT / 'Trexon.Web/Trexon.Web.csproj').findtext('PropertyGroup/Version'))
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='trexon-web-package-') as temporary:
        folder = Path(temporary)
        project = folder / 'Consumer.csproj'
        version = escape(args.version, {'"': '&quot;'})
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk.Web">
<PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><PackageReference Include="Trexon.Web" Version="{version}" /></ItemGroup>
</Project>''', encoding='utf-8')
        (folder / 'Program.cs').write_text('''using Trexon.Web;
using Trexon.Web.Sockets;
var app = TrexonApp.Create(args, api => api.Prefix = "/v1");
app.AddRoutes<GreetingRoutes>("/api").AddSocket<EchoSocket>("/api");
await app.RunAsync(int.Parse(Environment.GetEnvironmentVariable("PACKAGE_SMOKE_PORT")!));
[Routes("/hello")]
public sealed class GreetingRoutes
{
    [Get("{name}")]
    public ValueTask<object> Get(string name) => ValueTask.FromResult<object>(new { name });
}
[Socket("/ws/echo")]
public sealed class EchoSocket : SocketHandler<string>
{
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(message, token);
}
''', encoding='utf-8')
        # A new package cache proves the app does not depend on project references or a dev installation.
        environment = os.environ.copy()
        environment['NUGET_PACKAGES'] = str(folder / 'packages')
        environment['Logging__LogLevel__Default'] = 'Warning'
        restore = ['dotnet', 'restore', str(project), '--no-http-cache', '--source', 'https://api.nuget.org/v3/index.json']
        if args.source:
            restore += ['--source', str(Path(args.source).resolve())]
        for command in (restore, ['dotnet', 'build', str(project), '-c', 'Release', '--no-restore', '-warnaserror', '--nologo']):
            result = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True, text=True, timeout=240)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)
        assets = json.loads((folder / 'obj/project.assets.json').read_text(encoding='utf-8'))
        assert assets['libraries'][f'Trexon.Web/{args.version}']['type'] == 'package'
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        environment['PACKAGE_SMOKE_PORT'] = str(port)
        base = f'http://127.0.0.1:{port}/v1'
        log = folder / 'server.log'
        with log.open('w', encoding='utf-8') as output:
            process = subprocess.Popen(['dotnet', str(folder / 'bin/Release/net8.0/Consumer.dll')],
                                       cwd=folder, env=environment, stdout=output, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 30
                while True:
                    try:
                        with urllib.request.urlopen(base + '/api/hello/Ada', timeout=2) as response:
                            assert json.load(response) == {'name': 'Ada'}
                        break
                    except urllib.error.URLError:
                        if process.poll() is not None or time.monotonic() > deadline:
                            raise RuntimeError('Package consumer did not start:\n' + log.read_text(encoding='utf-8'))
                        time.sleep(.1)
                for path, marker in [('/docs/index.html', b'swagger-ui'), ('/openapi.json', b'/api/hello/{name}'),
                                     ('/docs/ws/', b'WebSocket'), ('/docs/ws/console.js', b'WebSocket'),
                                     ('/docs/ws/config.json', b'schemaUrl')]:
                    with urllib.request.urlopen(base + path, timeout=10) as response:
                        assert marker in response.read(), path
                with urllib.request.urlopen(base + '/openapi.json', timeout=10) as response:
                    schema = json.load(response)
                    assert schema['x-trexon-websockets'][0]['path'] == '/api/ws/echo'
                    assert schema['servers'][0]['url'] == '/v1'
                with urllib.request.urlopen(base + '/api/hello/Grace?token=excluded-from-log', timeout=10) as response:
                    assert json.load(response) == {'name': 'Grace'}
            finally:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
        text = log.read_text(encoding='utf-8')
        assert '[INF] HTTP GET /v1/api/hello/Grace -> 200' in text, text
        assert 'excluded-from-log' not in text, text
        print(f'Package smoke passed: Trexon.Web {args.version}, isolated NuGet cache, prefixes, HTTP, Swagger, WebSocket console resources, default Log.Info.')


if __name__ == '__main__':
    main()
