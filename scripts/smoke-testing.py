#!/usr/bin/env python3
"""Exercise the packaged test client on both supported runtimes, with an empty NuGet cache."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROGRAM = '''using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trexon.Web;
using Trexon.Web.Hosting;
using Trexon.Web.Security;
using Trexon.Web.Sockets;
using Trexon.Web.Testing;
var state = new State();
await using (var api = await TrexonTest.CreateAsync(app => {
    app.Services.AddSingleton(state);
    app.Services.AddScoped<ScopedValue>();
    app.Services.AddSingleton(new Value("production"));
    app.AddStartup<Startup>().AddRoutes<HelloRoutes>().AddSocket<MessageSocket>();
}, services => services.Replace(ServiceDescriptor.Singleton(new Value("fake"))))) {
    if (!state.Ready || state.Disposed != 1) throw new Exception("Startup scope was not awaited/disposed");
    if (await api.Client.GetStringAsync("/hello") != "fake") throw new Exception("DI override failed");
    if ((int)(await api.Client.GetAsync("/hello/user")).StatusCode != 401) throw new Exception("Anonymous identity must fail");
    using var ws = await api.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws/messages"), CancellationToken.None);
    async Task<string> Read() {
        await ws.SendAsync(Encoding.UTF8.GetBytes("hello").AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[1024];
        var result = await ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
    if (await Read() == await Read()) throw new Exception("Message DI scope reused");
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    if (state.Disposed != 3) throw new Exception("Message scope not disposed");
}
Console.WriteLine("Packaged Testing consumer passed: HTTP, overrides, scoped startup, current user, WebSocket message scopes and async disposal.");
public sealed record Value(string Text);
public sealed class State { public bool Ready; public int Disposed; }
public sealed class ScopedValue(State state) : IAsyncDisposable {
    public string Id { get; } = Guid.NewGuid().ToString();
    public async ValueTask DisposeAsync() { await Task.Yield(); Interlocked.Increment(ref state.Disposed); }
}
public sealed class Startup(State state, ScopedValue value) : ITrexonStartup {
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { GC.KeepAlive(value); state.Ready = true; return ValueTask.CompletedTask; }
}
[Routes("/hello")]
public sealed class HelloRoutes(Value value, ICurrentUser user) {
    [Get] public string Get() => value.Text;
    [Get("user")] public string User() => user.Id;
}
[Socket("/ws/messages")]
public sealed class MessageSocket(ScopedValue value) : SocketMessageHandler<string> {
    public override ValueTask OnMessageAsync(string message, SocketConnection<string> connection, CancellationToken token) => connection.SendAsync(value.Id, token);
}
'''

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path)
    parser.add_argument('--version', default=ET.parse(ROOT / 'Trexon.Web.Testing/Trexon.Web.Testing.csproj').findtext('PropertyGroup/Version'))
    args = parser.parse_args()
    # Version is a command argument, never shell code.
    if any(c not in '0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-' for c in args.version):
        raise ValueError('Invalid package version')
    with tempfile.TemporaryDirectory(prefix='trexon-testing-package-') as temporary:
        folder = Path(temporary)
        environment = dict(os.environ, NUGET_PACKAGES=str(folder / 'packages'))
        (folder / 'Program.cs').write_text(PROGRAM, encoding='utf-8')
        (folder / 'Consumer.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk.Web">
<PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><PackageReference Include="Trexon.Web.Testing" Version="{args.version}" /></ItemGroup></Project>''', encoding='utf-8')
        commands = [['dotnet', 'restore', str(folder / 'Consumer.csproj'), '--no-http-cache', '--source', 'https://api.nuget.org/v3/index.json']]
        if args.source:
            commands[0] += ['--source', str(args.source.resolve())]
        commands += [['dotnet', 'run', '--project', str(folder / 'Consumer.csproj'), '-c', 'Release', '-f', framework, '--no-restore', '-p:TreatWarningsAsErrors=true'] for framework in ('net8.0', 'net10.0')]
        for command in commands:
            result = subprocess.run(command, cwd=folder, env=environment, capture_output=True, text=True, timeout=600)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)
            if 'run' in command:
                print(result.stdout.strip(), flush=True)
    print('Testing package smoke passed on .NET 8 and .NET 10.')

if __name__ == '__main__':
    main()
