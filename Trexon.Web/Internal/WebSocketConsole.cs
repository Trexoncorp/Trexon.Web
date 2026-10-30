using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Trexon.Web.Internal;

// The console uses the same OpenAPI socket contracts as Swagger, with no second registry.
internal static class WebSocketConsole
{
    internal static void Map(WebApplication app, ApiOptions options, string? loginPath, bool cookies)
    {
        var path = options.DocsPath.TrimEnd('/') + "/ws";
        var root = new Uri("https://trexon.invalid" + path + "/");
        string Relative(string target) => root.MakeRelativeUri(new Uri("https://trexon.invalid" + target)).ToString();

        // Canonical trailing slash keeps assets and config relative to the console under any PathBase.
        app.MapGet(path, (HttpContext context) => context.Request.Path.Value!.EndsWith('/')
            ? Resource("WebSocketConsole.html", "text/html; charset=utf-8")
            : Results.Redirect(context.Request.PathBase + path + "/"))
            .AllowAnonymous().ExcludeFromDescription();
        app.MapGet(path + "/console.js", () => Resource("WebSocketConsole.js", "text/javascript; charset=utf-8"))
            .AllowAnonymous().ExcludeFromDescription();
        app.MapGet(path + "/config.json", () => Results.Json(new
        {
            title = options.Title,
            schemaUrl = Relative(options.OpenApiPath),
            appUrl = Relative("/"),
            docsUrl = "../",
            // Browser WebSocket cannot set arbitrary credential headers; use the existing cookie flow.
            auth = loginPath != null && cookies ? new
            {
                csrfUrl = Relative(loginPath[..^6] + "/cookie/csrf"),
                loginUrl = Relative(loginPath[..^6] + "/cookie/login"),
                logoutUrl = Relative(loginPath[..^6] + "/cookie/logout"),
                meUrl = Relative(loginPath[..^6] + "/me")
            } : null
        })).AllowAnonymous().ExcludeFromDescription();
    }

    private static IResult Resource(string name, string type) => Results.Stream(
        typeof(TrexonApp).Assembly.GetManifestResourceStream("Trexon.Web.Internal." + name)!, type);
}
