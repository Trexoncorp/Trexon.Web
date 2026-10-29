using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Trexon.Web;

/// <summary>An intentional HTTP error with a client-safe explanation.</summary>
public sealed class HttpError : Exception
{
    /// <summary>Creates an intentional error. Detail is returned to the client.</summary>
    public HttpError(int statusCode, string detail, string? title = null) : base(detail)
    {
        if (statusCode is < 400 or > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));
        StatusCode = statusCode;
        Title = title ?? Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(statusCode);
    }
    /// <summary>The HTTP response status.</summary>
    public int StatusCode { get; }
    /// <summary>The short error title.</summary>
    public string Title { get; }
}

/// <summary>Maps application exceptions to client-safe HTTP errors.</summary>
public sealed class ErrorOptions
{
    internal readonly Dictionary<Type, (int Status, string Title)> Mappings = [];
    /// <summary>Maps an exception type and its subclasses without exposing exception messages.</summary>
    public ErrorOptions Map<TException>(int statusCode, string title) where TException : Exception
    {
        if (statusCode is < 400 or > 599) throw new ArgumentOutOfRangeException(nameof(statusCode));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Mappings[typeof(TException)] = (statusCode, title);
        return this;
    }
}

/// <summary>Replace this service to customize exception responses.</summary>
public interface IExceptionResponder
{
    /// <summary>Writes an exception response before response headers have started.</summary>
    Task WriteAsync(HttpContext context, Exception exception);
}

internal sealed class DefaultExceptionResponder(ApiOptions api, ErrorOptions errors,
    ILogger<DefaultExceptionResponder> logger) : IExceptionResponder
{
    public Task WriteAsync(HttpContext context, Exception exception)
    {
        var status = 500;
        var title = "Internal Server Error";
        string? detail = null;
        if (exception is HttpError http) (status, title, detail) = (http.StatusCode, http.Title, http.Message);
        else if (exception is BadHttpRequestException bad) (status, title) = (bad.StatusCode, "Invalid HTTP request");
        else
        {
            for (var type = exception.GetType(); type != null; type = type.BaseType)
                if (errors.Mappings.TryGetValue(type, out var mapped))
                { (status, title) = mapped; break; }
            if (api.IncludeExceptionDetails) detail = exception.ToString();
        }
        if (status >= 500) logger.LogError(exception, "Unhandled HTTP exception. TraceId: {TraceId}", context.TraceIdentifier);
        return Problems.WriteAsync(context, status, title, detail);
    }
}

internal static class Problems
{
    internal static ProblemDetails Create(HttpContext context, int status, string title, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{status}",
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        return problem;
    }
    internal static Task WriteAsync(HttpContext context, int status, string title, string? detail = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Create(context, status, title, detail),
            options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json",
            cancellationToken: context.RequestAborted);
    }
    internal static ValidationProblemDetails Validation(HttpContext context, IDictionary<string, string[]> errors)
    {
        var problem = new ValidationProblemDetails(errors)
        {
            Status = 400,
            Title = "Request validation failed",
            Type = "https://httpstatuses.io/400",
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        return problem;
    }
}
