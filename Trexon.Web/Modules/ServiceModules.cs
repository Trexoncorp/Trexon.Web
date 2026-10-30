using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Trexon.Logging;

namespace Trexon.Web.Modules;

/// <summary>Enables CORS for explicit trusted origins.</summary>
public sealed class CorsModule : ITrexonModule
{
    private readonly string[] _origins;
    private readonly bool _credentials;
    /// <summary>Creates a policy. Wildcard origins with credentials are rejected.</summary>
    public CorsModule(string[] origins, bool allowCredentials = false)
    {
        if (origins.Length == 0 || origins.Any(o => string.IsNullOrWhiteSpace(o) || o == "*"))
            throw new ArgumentException("Provide explicit origins, without wildcards.", nameof(origins));
        _origins = origins.ToArray(); _credentials = allowCredentials;
    }
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        app.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(_origins).AllowAnyHeader().AllowAnyMethod();
            if (_credentials) policy.AllowCredentials();
        }));
        app.HasCors = true;
    }
}

/// <summary>Enables standard [OutputCache] attributes. The default policy excludes authenticated responses.</summary>
public sealed class OutputCacheModule : ITrexonModule
{
    /// <inheritdoc />
    public void Configure(TrexonApp app) { app.Services.AddOutputCache(); app.HasOutputCache = true; }
}

/// <summary>Configures a per-user, otherwise per-remote-IP, fixed-window request limit.</summary>
public sealed class RateLimitModule : ITrexonModule
{
    private readonly int _permits;
    private readonly TimeSpan _window;
    /// <summary>Creates an in-process limit without queuing requests.</summary>
    public RateLimitModule(int permits = 100, TimeSpan? window = null)
    {
        if (permits <= 0) throw new ArgumentOutOfRangeException(nameof(permits));
        _permits = permits; _window = window ?? TimeSpan.FromMinutes(1);
        if (_window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
    }
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        app.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.IsAuthenticated == true
                    ? "user:" + (context.User.FindFirst("sub")?.Value ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity.Name ?? "unknown")
                    : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = _permits, Window = _window, QueueLimit = 0 }));
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new ValueTask(Problems.WriteAsync(context.HttpContext, 429, "Too Many Requests"));
            };
        });
        app.HasRateLimiter = true;
    }
}

/// <summary>Adds a JSON health endpoint and standard IHealthCheck registrations.</summary>
public sealed class HealthChecksModule(string path = "/health") : ITrexonModule
{
    /// <inheritdoc />
    public void Configure(TrexonApp app)
    {
        if (!path.StartsWith('/')) throw new ArgumentException("Health path must start with /.", nameof(path));
        app.Services.AddHealthChecks();
        app.Map(web => web.MapHealthChecks(path, new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.ToDictionary(p => p.Key, p => new { status = p.Value.Status.ToString() })
            }, context.RequestAborted)
        }));
    }
}

/// <summary>A completed HTTP request. Bodies, query strings, cookies, and credentials are excluded.</summary>
public sealed record RequestLog(string Method, string Path, int StatusCode, double ElapsedMilliseconds, string TraceId);

/// <summary>Customizes the default Log.Info request logging, including rejected and failed requests.</summary>
public sealed class RequestLoggingModule : ITrexonModule
{
    private readonly Func<RequestLog, CancellationToken, ValueTask>? _write;
    /// <summary>Uses Trexon.Logging.Log.Info by default, or replaces it with a synchronous sink.</summary>
    public RequestLoggingModule(Action<RequestLog>? write = null)
    {
        if (write != null) _write = (entry, _) => { write(entry); return ValueTask.CompletedTask; };
    }
    /// <summary>Uses an asynchronous sink, awaited before the request pipeline completes.</summary>
    public RequestLoggingModule(Func<RequestLog, CancellationToken, ValueTask> write) => _write = write;
    /// <inheritdoc />
    public void Configure(TrexonApp app) => app.RequestLogWriter = _write ?? WriteDefault;

    internal static ValueTask WriteDefault(RequestLog entry, CancellationToken cancellationToken)
    {
        Log.Info("HTTP {Method} {Path} -> {StatusCode} in {ElapsedMs:F2}ms [{TraceId}]",
            entry.Method, entry.Path, entry.StatusCode, entry.ElapsedMilliseconds, entry.TraceId);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Convenience methods for standard service modules.</summary>
public static class ServiceModuleExtensions
{
    /// <summary>Enables a trusted-origin CORS policy.</summary>
    public static TrexonApp UseCors(this TrexonApp app, params string[] origins) => app.AddModule(new CorsModule(origins));
    /// <summary>Enables attribute-based public response caching.</summary>
    public static TrexonApp UseOutputCache(this TrexonApp app) => app.AddModule<OutputCacheModule>();
    /// <summary>Enables a per-client fixed-window limiter.</summary>
    public static TrexonApp UseRateLimit(this TrexonApp app, int permits = 100, TimeSpan? window = null) => app.AddModule(new RateLimitModule(permits, window));
    /// <summary>Enables the health endpoint.</summary>
    public static TrexonApp UseHealthChecks(this TrexonApp app, string path = "/health") => app.AddModule(new HealthChecksModule(path));
    /// <summary>Replaces automatic Log.Info request logging with a callback, or restores the default sink.</summary>
    public static TrexonApp UseRequestLogging(this TrexonApp app, Action<RequestLog>? write = null) => app.AddModule(new RequestLoggingModule(write));
    /// <summary>Awaits an asynchronous request-log sink without creating background jobs.</summary>
    public static TrexonApp UseRequestLogging(this TrexonApp app, Func<RequestLog, CancellationToken, ValueTask> write) => app.AddModule(new RequestLoggingModule(write));
}
