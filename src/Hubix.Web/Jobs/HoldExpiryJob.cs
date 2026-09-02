using Hubix.Infrastructure;
using Hubix.Infrastructure.Sales;
using Hubix.Web.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Jobs;

/// <summary>
/// Releases holds whose countdown has run out, and tells the waiting list.
/// </summary>
/// <remarks>
/// <para>
/// Runs every minute under Hangfire, which persists its schedule in the same database, retries
/// on failure and holds a distributed lock so a scaled-out app cannot double-fire.
/// </para>
/// <para>
/// A silently dead expiry job is invisible and corrosive: expired holds stay Held, and units
/// nobody wants sit off the market. The last-success timestamp is what to alert on.
/// </para>
/// </remarks>
public sealed class HoldExpiryJob(
    HoldService holds,
    WaitlistPromoter promoter,
    HoldNotifier notifier,
    HubixDbContext db,
    TimeProvider clock,
    ILogger<HoldExpiryJob> logger)
{
    /// <summary>Hangfire identifies the recurring job by this.</summary>
    public const string RecurringJobId = "expire-holds";

    public static DateTimeOffset? LastSuccessAt { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var expired = await holds.ExpireDueHoldsAsync(ct);

        foreach (var hold in expired)
        {
            // Notifications must never take the job down. A hold that released but failed to
            // send an email is a far better outcome than one that stayed stuck because the
            // mail provider was briefly down.
            try
            {
                await notifier.HoldExpiredAsync(hold, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not tell {Email} that their hold on {UnitCode} expired",
                    hold.ProspectEmail, hold.UnitCode);
            }

            try
            {
                await promoter.PromoteAsync(hold.UnitId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not notify the waiting list that {UnitCode} came free", hold.UnitCode);
            }
        }

        LastSuccessAt = clock.GetUtcNow();
    }

    /// <summary>
    /// Surfaces holds that expired but somehow never released, so the admin can see them.
    /// </summary>
    /// <remarks>
    /// If the job dies, this is the number that grows. Recoverable, not silent.
    /// </remarks>
    public Task<int> CountStuckHoldsAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddMinutes(-5);

        return db.Holds.CountAsync(h => h.ReleasedAt == null && h.ExpiresAt <= cutoff, ct);
    }
}
