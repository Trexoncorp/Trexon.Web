using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Trexon.Web;

/// <summary>A field-level validation error.</summary>
public sealed record ValidationIssue(string Field, string Message);

/// <summary>Adds async business validation in addition to DataAnnotations.</summary>
public interface IRequestValidator<in T>
{
    /// <summary>Checks a model before its handler runs.</summary>
    ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(T model, CancellationToken cancellationToken);
}

internal interface IValidationRegistration
{
    ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(object model, IServiceProvider services, CancellationToken token);
}
internal sealed class ValidationRegistration<T> : IValidationRegistration
{
    public async ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(object model, IServiceProvider services, CancellationToken token)
    {
        if (model is not T typed) return [];
        var issues = new List<ValidationIssue>();
        foreach (var validator in services.GetServices<IRequestValidator<T>>())
            issues.AddRange(await validator.ValidateAsync(typed, token));
        return issues;
    }
}
internal sealed class ValidationFilter(IEnumerable<IValidationRegistration> registrations) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var issues = new List<ValidationIssue>();
        foreach (var argument in context.ActionArguments.Values.Where(v => v != null))
            foreach (var registration in registrations)
                issues.AddRange(await registration.ValidateAsync(argument!, context.HttpContext.RequestServices,
                    context.HttpContext.RequestAborted));
        if (issues.Count > 0)
        {
            context.Result = new BadRequestObjectResult(Problems.Validation(context.HttpContext,
                issues.GroupBy(i => i.Field).ToDictionary(g => g.Key, g => g.Select(i => i.Message).Distinct().ToArray())));
            return;
        }
        await next();
    }
}
