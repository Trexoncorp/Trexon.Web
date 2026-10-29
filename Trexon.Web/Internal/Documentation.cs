using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Trexon.Web.Internal;

internal sealed record SecurityDescription(string Id, SecuritySchemeType Type, string? Scheme = null,
    string? Header = null, ParameterLocation Location = ParameterLocation.Header);

internal sealed class OperationDocumentation(List<SecurityDescription> schemes) : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var method = context.MethodInfo;
        var group = method.DeclaringType!;
        var summary = method.GetCustomAttribute<SummaryAttribute>() ?? group.GetCustomAttribute<SummaryAttribute>();
        if (summary != null) { operation.Summary = summary.Text; operation.Description = summary.Description; }
        var tags = method.GetCustomAttributes<TagAttribute>().Select(t => t.Name).ToArray();
        if (tags.Length == 0) tags = group.GetCustomAttributes<TagAttribute>().Select(t => t.Name).ToArray();
        if (tags.Length == 0 && (operation.Tags == null || operation.Tags.Count == 0)) tags = [group.Name.Replace("Routes", "", StringComparison.Ordinal)];
        if (tags.Length > 0) operation.Tags = tags.Select(t => new OpenApiTagReference(t, context.Document)).ToHashSet();
        operation.OperationId ??= $"{group.Name}_{method.Name}_{context.ApiDescription.HttpMethod}_{Uri.EscapeDataString(context.ApiDescription.RelativePath ?? "")}";
        var attributes = group.GetCustomAttributes(true).Concat(method.GetCustomAttributes(true))
            .Concat(context.ApiDescription.ActionDescriptor.EndpointMetadata).ToArray();
        var authorized = attributes.OfType<IAuthorizeData>().ToArray();
        if (attributes.OfType<IAllowAnonymous>().Any() || authorized.Length == 0) return;
        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Authentication required" });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Insufficient permissions" });
        var named = authorized.SelectMany(a => (a.AuthenticationSchemes ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
        operation.Security = schemes.Where(s => named.Count == 0 || named.Contains(s.Id)).Select(s => new OpenApiSecurityRequirement
        { [new OpenApiSecuritySchemeReference(s.Id, context.Document)] = [] }).ToList();
    }
}

internal static class SchemaNames
{
    internal static string For(Type type)
    {
        if (type.IsArray) return For(type.GetElementType()!) + "Array";
        if (type.IsGenericType)
            return (type.GetGenericTypeDefinition().FullName ?? type.Name).Split('`')[0].Replace('+', '.') +
                "Of" + string.Join("And", type.GetGenericArguments().Select(For));
        return (type.FullName ?? type.Name).Replace('+', '.');
    }
}

internal sealed class DocumentTags : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        var used = document.Paths.Values.SelectMany(path => path.Operations?.Values.AsEnumerable() ?? [])
            .SelectMany(operation => operation.Tags?.AsEnumerable() ?? []).Select(tag => tag.Name).Where(name => name != null).Distinct();
        var existing = document.Tags?.ToArray() ?? [];
        document.Tags = used.Select(name => existing.FirstOrDefault(tag => tag.Name == name) ?? new OpenApiTag { Name = name! }).ToHashSet();
    }
}
