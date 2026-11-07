using Keystone.Infrastructure;
using Keystone.Infrastructure.Assets;
using Keystone.Web.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Keystone.Web.Health;

/// <summary>
/// Is the database there.
/// </summary>
/// <remarks>
/// A cheap round trip rather than a query. Anything heavier turns a health endpoint polled
/// every few seconds into load of its own.
/// </remarks>
public sealed class DatabaseHealthCheck(KeystoneDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        return await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy("Database reachable.")
            : HealthCheckResult.Unhealthy("Cannot reach the database.");
    }
}

/// <summary>
/// Is the hold expiry job still running.
/// </summary>
/// <remarks>
/// <para>
/// Measured by its consequence rather than by a heartbeat: the number of holds whose countdown
/// finished a while ago and which are still held. A heartbeat only proves something ran; this
/// proves the thing it was supposed to do got done.
/// </para>
/// <para>
/// It also survives a restart. An in-memory "last ran at" is null for the first minute after
/// every deploy, which would either cry wolf or be given so much slack that a genuinely dead
/// job goes unnoticed for an hour.
/// </para>
/// <para>
/// This is the alert worth having. A dead expiry job is invisible from the outside — expired
/// holds simply stay held, apartments nobody wants stay off the market, and the waiting list
/// hears nothing — and the first person to notice is a buyer who was told an apartment was
/// taken when it was not.
/// </para>
/// </remarks>
public sealed class HoldExpiryHealthCheck(HoldExpiryJob job) : IHealthCheck
{
    /// <summary>One or two in flight is the job mid-sweep. A pile is a stopped job.</summary>
    private const int Tolerated = 3;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var stuck = await job.CountStuckHoldsAsync(ct);

        var data = new Dictionary<string, object>
        {
            ["stuckHolds"] = stuck,
            ["lastSuccessAt"] = HoldExpiryJob.LastSuccessAt?.ToString("u") ?? "not since startup"
        };

        return stuck > Tolerated
            ? HealthCheckResult.Unhealthy(
                $"{stuck} holds expired but were never released. The expiry job has stopped.", data: data)
            : HealthCheckResult.Healthy("Holds are being expired.", data);
    }
}

/// <summary>Can files be reached — a missing container is invisible until somebody uploads.</summary>
public sealed class AssetStorageHealthCheck(IAssetStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        try
        {
            // Asking for a file that does not exist still exercises the connection, without
            // writing anything on every poll.
            await storage.OpenAsync("health-check-probe", ct);

            return HealthCheckResult.Healthy(storage.Description);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Cannot reach {storage.Description}.", ex);
        }
    }
}
