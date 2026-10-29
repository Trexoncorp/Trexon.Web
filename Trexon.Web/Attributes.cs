using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.OutputCaching;

namespace Trexon.Web;

/// <summary>Declares a route group with API binding and automatic DataAnnotations validation.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class RoutesAttribute(string prefix = "") : RouteAttribute(prefix), IApiBehaviorMetadata;

/// <summary>Maps a GET handler.</summary>
public sealed class GetAttribute(string template = "") : HttpGetAttribute(template);
/// <summary>Maps a POST handler.</summary>
public sealed class PostAttribute(string template = "") : HttpPostAttribute(template);
/// <summary>Maps a PUT handler.</summary>
public sealed class PutAttribute(string template = "") : HttpPutAttribute(template);
/// <summary>Maps a PATCH handler.</summary>
public sealed class PatchAttribute(string template = "") : HttpPatchAttribute(template);
/// <summary>Maps a DELETE handler.</summary>
public sealed class DeleteAttribute(string template = "") : HttpDeleteAttribute(template);
/// <summary>Maps a HEAD handler.</summary>
public sealed class HeadAttribute(string template = "") : HttpHeadAttribute(template);
/// <summary>Maps an OPTIONS handler.</summary>
public sealed class OptionsAttribute(string template = "") : HttpOptionsAttribute(template);

/// <summary>Groups operations in OpenAPI; method tags override group tags.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class TagAttribute(string name) : Attribute
{
    /// <summary>The displayed tag.</summary>
    public string Name { get; } = name;
}

/// <summary>Describes an operation in OpenAPI.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class SummaryAttribute(string text) : Attribute
{
    /// <summary>The short operation summary.</summary>
    public string Text { get; } = text;
    /// <summary>Optional long Markdown description.</summary>
    public string? Description { get; set; }
}

/// <summary>Declares a documented response status and optional body type.</summary>
public sealed class ResponseAttribute : ProducesResponseTypeAttribute
{
    /// <summary>Declares a response without a body.</summary>
    public ResponseAttribute(int statusCode) : base(statusCode) { }
    /// <summary>Declares a typed response.</summary>
    public ResponseAttribute(int statusCode, Type bodyType) : base(bodyType, statusCode) { }
}
