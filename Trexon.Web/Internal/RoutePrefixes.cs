using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Trexon.Web.Internal;

internal static class RoutePrefixes
{
    internal static string Normalize(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix is "" or "/") return "";
        if (!prefix.StartsWith('/') || prefix.Contains("//", StringComparison.Ordinal) ||
            prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '-' or '_' or '.' or '~')) ||
            prefix.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("A prefix must be an absolute literal path, such as /v1 or /api/v1, without empty or dot segments.", nameof(prefix));
        return prefix.TrimEnd('/');
    }

    internal static string Combine(string prefix, string? route)
    {
        var path = route ?? "";
        if (path.StartsWith("~/", StringComparison.Ordinal)) path = path[2..];
        return prefix + "/" + path.TrimStart('/');
    }
}

internal sealed class RegisteredRoutePrefixes(Dictionary<Type, string> prefixes) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
            if (prefixes.TryGetValue(controller.ControllerType.AsType(), out var prefix) && prefix.Length > 0)
            {
                foreach (var selector in controller.Selectors)
                    if (selector.AttributeRouteModel is { } route)
                        route.Template = RoutePrefixes.Combine(prefix, route.Template);
                foreach (var action in controller.Actions)
                    foreach (var selector in action.Selectors)
                        if (selector.AttributeRouteModel is { IsAbsoluteTemplate: true } route)
                            // An absolute action may bypass its class route, but not the registration prefix.
                            route.Template = RoutePrefixes.Combine(prefix, route.Template);
            }
    }
}
