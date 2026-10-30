namespace Trexon.Web;

/// <summary>Documentation and error-response defaults.</summary>
public sealed class ApiOptions
{
    /// <summary>OpenAPI title.</summary>
    public string Title { get; set; } = "Trexon API";
    /// <summary>OpenAPI version.</summary>
    public string Version { get; set; } = "v1";
    /// <summary>Optional Markdown API description.</summary>
    public string? Description { get; set; }
    /// <summary>Swagger UI path, including the leading slash. The WebSocket console is available at DocsPath + /ws.</summary>
    public string DocsPath { get; set; } = "/docs";
    /// <summary>OpenAPI JSON path.</summary>
    public string OpenApiPath { get; set; } = "/openapi.json";
    /// <summary>Whether Swagger UI, the WebSocket console, and the OpenAPI document are exposed.</summary>
    public bool EnableDocs { get; set; } = true;
    /// <summary>Opt-in exception details. Keep false on publicly accessible services.</summary>
    public bool IncludeExceptionDetails { get; set; }
}
