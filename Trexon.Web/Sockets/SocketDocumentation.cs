using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Trexon.Web.Sockets;

internal sealed class SocketDocumentation(List<SocketEndpoint> endpoints) : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        if (endpoints.Count == 0) return;
        var descriptions = new JsonArray();
        var lines = new List<string> { "", "### WebSockets", "", "[Open WebSocket console](./ws) to connect, send messages and inspect responses. HTTP Try it out does not exchange WebSocket messages.", "", "| Endpoint | Input → output | Access |", "| --- | --- | --- |" };
        foreach (var endpoint in endpoints)
        {
            var metadata = endpoint.HandlerType.GetCustomAttributes(true);
            var anonymous = metadata.OfType<IAllowAnonymous>().Any();
            var authorization = anonymous ? [] : metadata.OfType<IAuthorizeData>().ToArray();
            var summary = endpoint.HandlerType.GetCustomAttribute<SummaryAttribute>();
            var input = context.SchemaGenerator.GenerateSchema(endpoint.InputType, context.SchemaRepository);
            var output = context.SchemaGenerator.GenerateSchema(endpoint.OutputType, context.SchemaRepository);
            descriptions.Add(new JsonObject
            {
                ["path"] = endpoint.Path,
                ["segments"] = new JsonArray(RoutePatternFactory.Parse(endpoint.Path).PathSegments.Select(segment =>
                    (JsonNode)new JsonArray(segment.Parts.Select(Part).ToArray())).ToArray()),
                ["summary"] = summary?.Text,
                ["description"] = summary?.Description,
                ["tags"] = new JsonArray(endpoint.HandlerType.GetCustomAttributes<TagAttribute>().Select(tag => (JsonNode?)JsonValue.Create(tag.Name)).ToArray()),
                ["input"] = Schema(input),
                ["output"] = Schema(output),
                ["inputFormat"] = endpoint.InputType == typeof(string) ? "text" : "json",
                ["outputFormat"] = endpoint.OutputType == typeof(string) ? "text" : "json",
                ["authorization"] = System.Text.Json.JsonSerializer.SerializeToNode(authorization.Select(data => new { roles = data.Roles, policy = data.Policy, schemes = data.AuthenticationSchemes })),
                ["allowAnonymous"] = anonymous
            });
            lines.Add($"| `{endpoint.Path.Replace("|", "\\|", StringComparison.Ordinal)}` | `{endpoint.InputType.Name}` → `{endpoint.OutputType.Name}` | {(authorization.Length > 0 ? "Authenticated" : anonymous ? "Anonymous" : "Application default")} |");
        }
        context.SchemaGenerator.GenerateSchema(typeof(SocketErrorMessage), context.SchemaRepository);
        document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        document.Extensions["x-trexon-websockets"] = new JsonNodeExtension(descriptions);
        document.Info.Description = (document.Info.Description ?? "") + string.Join("\n", lines);
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas = context.SchemaRepository.Schemas;
    }
    private static JsonNode Part(RoutePatternPart part) => part switch
    {
        RoutePatternParameterPart parameter => new JsonObject
        {
            ["parameter"] = parameter.Name, ["optional"] = parameter.IsOptional,
            ["catchAll"] = parameter.IsCatchAll, ["default"] = parameter.Default?.ToString()
        },
        RoutePatternSeparatorPart separator => new JsonObject { ["text"] = separator.Content, ["separator"] = true },
        RoutePatternLiteralPart literal => new JsonObject { ["text"] = literal.Content },
        _ => throw new InvalidOperationException("Unsupported WebSocket route part.")
    };
    private static JsonNode? Schema(IOpenApiSchema schema)
    {
        using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        schema.SerializeAsV3(new OpenApiJsonWriter(text));
        return JsonNode.Parse(text.ToString());
    }
}
