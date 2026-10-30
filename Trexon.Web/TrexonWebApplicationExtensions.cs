using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Trexon.Web;

/// <summary>Shortcuts for running a built application.</summary>
public static class TrexonWebApplicationExtensions
{
    /// <summary>Runs HTTP on 127.0.0.1 at the supplied port, overriding configured listening addresses.</summary>
    /// <param name="app">An application that has been built but not started.</param>
    /// <param name="port">A port from 0 to 65535. Zero lets the operating system choose an available port.</param>
    /// <param name="cancellationToken">Requests graceful shutdown.</param>
    /// <returns>A task that completes when the host stops and has been disposed.</returns>
    public static Task RunAsync(this WebApplication app, int port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ValidatePort(port);
        cancellationToken.ThrowIfCancellationRequested();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The configured server does not support listening addresses.");
        addresses.Addresses.Clear();
        addresses.Addresses.Add("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
        // An explicit port also takes precedence over Kestrel Listen/Endpoints configuration.
        addresses.PreferHostingUrls = true;
        return ((IHost)app).RunAsync(cancellationToken);
    }

    internal static void ValidatePort(int port)
    {
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");
    }
}
