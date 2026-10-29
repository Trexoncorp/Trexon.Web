using Microsoft.AspNetCore.Builder;

namespace Trexon.Web;

/// <summary>Registers a cohesive feature without assembly scanning or background tasks.</summary>
public interface ITrexonModule
{
    /// <summary>Registers routes, services, policies, and other modules before BuildAsync.</summary>
    void Configure(TrexonApp app);
    /// <summary>Completes asynchronous startup work before any HTTP requests are accepted.</summary>
    ValueTask InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    /// <summary>Adds middleware after authentication/authorization and before endpoint execution.</summary>
    void Use(WebApplication app) { }
}
