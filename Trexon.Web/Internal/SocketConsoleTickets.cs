using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Trexon.Web.Sockets;

namespace Trexon.Web.Internal;

// A bounded, process-local bridge for browser testing. Never proxies network traffic,
// changes the identity's scheme or puts the user's credential in the WebSocket URL.
internal sealed class SocketConsoleTickets(TimeProvider time, List<SecurityDescription> schemes, string apiKeyHeader)
{
    private const string ProtocolPrefix = "trexon.docs.";
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private sealed record Ticket(string Target, string Header, string Credential, DateTimeOffset ExpiresAt);
    private sealed record TicketRequest(string Path, string Scheme);

    internal async Task<IResult> IssueAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!SameOrigin(context, allowMissing: true)) return Problem(403, "Console tickets require the same origin.");
        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit?.IsReadOnly == false) bodyLimit.MaxRequestBodySize = 4096;
        if (context.Request.ContentLength > 4096) return Problem(413, "Ticket request is too large.");
        if (!context.Request.HasJsonContentType()) return Problem(415, "Use application/json for ticket requests.");
        TicketRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<TicketRequest>(context.RequestAborted); }
        catch (JsonException) { return Problem(400, "Provide a JSON path and authentication scheme."); }
        catch (BadHttpRequestException error) { return Problem(error.StatusCode, "Invalid ticket request."); }
        if (request == null || !TryTarget(request.Path, out var target)) return Problem(400, "Provide an application-relative socket path.");
        return await IssueForEndpointAsync(context, request, target);
    }

    // Set from the built application's endpoint sources; only explicit socket routes are eligible.
    private IEndpointRouteBuilder _endpoints = null!;

    private async Task<IResult> IssueForEndpointAsync(HttpContext context, TicketRequest request, string target)
    {
        var path = target.Split('?')[0];
        var socketRoute = _endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<SocketAttribute>() != null)
            .Any(endpoint => new TemplateMatcher(TemplateParser.Parse(endpoint.RoutePattern.RawText!), new RouteValueDictionary())
                .TryMatch(path, new RouteValueDictionary()));
        if (!socketRoute) return Problem(400, "The target must be a registered WebSocket endpoint.");
        if (request.Scheme is not ("Bearer" or "ApiKey") || !schemes.Any(scheme => scheme.Id == request.Scheme))
            return Problem(400, "This authentication scheme is not configured.");
        var header = request.Scheme == "Bearer" ? "Authorization" : apiKeyHeader;
        var values = context.Request.Headers[header];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 16384 ||
            request.Scheme == "Bearer" && !values[0]!.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Problem(401, "Provide valid credentials for the selected scheme.");
        var authenticated = await context.AuthenticateAsync(request.Scheme);
        if (!authenticated.Succeeded) return Problem(401, "Invalid or expired credentials.");
        var now = time.GetUtcNow();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            foreach (var expired in _tickets.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) _tickets.Remove(expired);
            if (_tickets.Count >= 256) return Problem(429, "Too many pending console connections. Try again shortly.");
            _tickets.Add(id, new(target, header, values[0]!, now.AddSeconds(30)));
        }
        return Results.Json(new { protocol = ProtocolPrefix + id, expiresIn = 30 });
    }

    internal void Use(WebApplication app)
    {
        _endpoints = app;
        app.Lifetime.ApplicationStopped.Register(() => { lock (_gate) _tickets.Clear(); });
        // Runs after routing and WebSocket transport, before the normal authentication middleware.
        app.Use(async (context, next) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { await next(context); return; }
            var protocols = context.Request.Headers.GetCommaSeparatedValues("Sec-WebSocket-Protocol");
            var offered = protocols.Where(protocol => protocol.StartsWith(ProtocolPrefix, StringComparison.Ordinal)).ToArray();
            if (offered.Length == 0) { await next(context); return; }
            if (!SameOrigin(context, allowMissing: false)) { await Problems.WriteAsync(context, 403, "Console connections require the same origin."); return; }
            Ticket? ticket = null;
            if (offered.Length == 1)
                lock (_gate) _tickets.Remove(offered[0][ProtocolPrefix.Length..], out ticket);
            if (ticket == null || ticket.ExpiresAt <= time.GetUtcNow() ||
                ticket.Target != context.Request.Path.Value + context.Request.QueryString.Value ||
                context.GetEndpoint()?.Metadata.GetMetadata<SocketAttribute>() == null)
            { await Problems.WriteAsync(context, 401, "Invalid, expired or already-used console ticket."); return; }
            if (context.Request.Headers.ContainsKey("Authorization") || context.Request.Headers.ContainsKey(apiKeyHeader))
            { await Problems.WriteAsync(context, 400, "Do not combine console tickets with credential headers."); return; }
            context.Request.Headers[ticket.Header] = ticket.Credential;
            // Browsers require the server to acknowledge the offered protocol. Only the
            // consumed random ticket is negotiated, never the user's token or API key.
            context.Features.Set(new ConsoleSocketProtocol(offered[0]));
            // The ordinary handlers revalidate credentials, including expiry/revocation, on upgrade.
            // Scheme-specific [Authorize], policies, roles and the original handler all remain intact.
            await next(context);
        });
    }

    private static bool TryTarget(string? path, out string target)
    {
        target = "";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || !path.StartsWith('/') || path.StartsWith("//") ||
            path.Contains('\\') || path.Contains('#') || !Uri.TryCreate("https://trexon.invalid" + path, UriKind.Absolute, out var uri)) return false;
        target = PathString.FromUriComponent(uri.AbsolutePath).Value + uri.Query;
        return true;
    }
    private static bool SameOrigin(HttpContext context, bool allowMissing)
    {
        var values = context.Request.Headers.Origin;
        return values.Count == 0 ? allowMissing : values.Count == 1 &&
            SocketOptions.TryOrigin(values[0]!, out var origin) &&
            SocketOptions.TryOrigin(context.Request.Scheme + "://" + context.Request.Host, out var own) &&
            string.Equals(origin, own, StringComparison.OrdinalIgnoreCase);
    }
    private static IResult Problem(int status, string detail) => Results.Problem(statusCode: status, detail: detail);
}

internal sealed record ConsoleSocketProtocol(string Value);
