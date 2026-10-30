using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using DataSource = Trexon.Data.Data;

namespace TrexonApi;

public sealed class NotesHealthCheck(DataSource data) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var db = data.Open();
        // Check the application table as well as the connection.
        await db.Table<Note>().AsNoTracking().AnyAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
