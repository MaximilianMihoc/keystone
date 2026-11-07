using Keystone.Domain;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Web.Notifications;

/// <summary>
/// Tells the next person in the queue that an apartment came back.
/// </summary>
/// <remarks>
/// One path, because there is more than one way a unit returns to sale: a hold running out,
/// and a reservation being cancelled. The second only became possible with the pipeline, and
/// a queue that hears about the first but not the second is worse than no queue at all —
/// people would learn it cannot be trusted and go back to phoning.
/// </remarks>
public sealed class WaitlistPromoter(
    WaitlistService waitlist,
    HoldNotifier notifier,
    KeystoneDbContext db,
    ILogger<WaitlistPromoter> logger)
{
    /// <returns>True when somebody was told.</returns>
    public async Task<bool> PromoteAsync(Guid unitId, CancellationToken ct)
    {
        var unit = await db.Units
            .AsNoTracking()
            .Where(u => u.Id == unitId)
            .Select(u => new
            {
                u.Code,
                u.State,
                u.ProjectId,
                u.UnitTypeId,
                ProjectName = u.Project!.Name,
                ProjectSlug = u.Project.Slug
            })
            .FirstOrDefaultAsync(ct);

        // Only ever say an apartment is free about an apartment that is free. Between the
        // release and this line somebody may already have taken it.
        if (unit is null || unit.State != UnitState.Available)
        {
            return false;
        }

        var candidate = await waitlist.NextInLineAsync(unit.ProjectId, unit.UnitTypeId, ct);

        if (candidate is null)
        {
            return false;
        }

        await notifier.UnitFreedAsync(candidate, unit.ProjectName, unit.ProjectSlug, unit.Code, ct);
        await waitlist.MarkNotifiedAsync(candidate.EntryId, ct);

        logger.LogInformation(
            "Told the next person on the list that {UnitCode} is free again", unit.Code);

        return true;
    }
}
