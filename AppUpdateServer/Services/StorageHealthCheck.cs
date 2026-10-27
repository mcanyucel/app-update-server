using AppUpdateServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AppUpdateServer.Services;

/// <summary>
/// Reports unhealthy when either of the two things the server cannot work
/// without — the database and a writable binaries directory — is unavailable.
/// A missing or read-only /data mount is the usual way this fails.
/// </summary>
public class StorageHealthCheck(AppDbContext db, IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("Database unreachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable", ex);
        }

        var binariesPath = UpdateService.GetBinariesPath(config);
        var probePath = Path.Combine(binariesPath, $".health-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(binariesPath);
            await File.WriteAllBytesAsync(probePath, [], cancellationToken);
            File.Delete(probePath);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"'{binariesPath}' is not writable", ex);
        }

        return HealthCheckResult.Healthy();
    }
}
