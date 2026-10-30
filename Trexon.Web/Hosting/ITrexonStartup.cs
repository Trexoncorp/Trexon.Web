namespace Trexon.Web.Hosting;

/// <summary>A scoped startup operation, awaited before the HTTP listener opens.</summary>
public interface ITrexonStartup
{
    /// <summary>Performs startup I/O. Dependencies and the handler are disposed asynchronously afterwards.</summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
