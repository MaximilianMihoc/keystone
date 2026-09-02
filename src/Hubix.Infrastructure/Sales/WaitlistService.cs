using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Sales;

public sealed record WaitlistCandidate(
    Guid EntryId,
    Guid ProspectId,
    string Email,
    string Locale,
    int Position);

/// <summary>
/// The queue for a unit type. First come, first served, as Hubix's documents state.
/// </summary>
/// <remarks>
/// Signing up is M6. This exists now because releasing a hold has to notify the queue, and a
/// release path that quietly drops that notification would be a half-built expiry job.
/// </remarks>
public sealed class WaitlistService(
    HubixDbContext db,
    TimeProvider clock,
    ILogger<WaitlistService> logger)
{
    /// <summary>
    /// Who to tell that a unit came free: the longest waiting person for that type, then the
    /// longest waiting person who will take anything.
    /// </summary>
    public async Task<WaitlistCandidate?> NextInLineAsync(
        Guid projectId,
        Guid? unitTypeId,
        CancellationToken ct) =>
        await db.WaitlistEntries
            .Where(w => w.ProjectId == projectId
                        && w.RemovedAt == null
                        && w.NotifiedAt == null
                        && (w.UnitTypeId == unitTypeId || w.UnitTypeId == null))
            // Someone waiting for this exact type is a better match than someone waiting
            // for anything, so type-specific entries come first.
            .OrderBy(w => w.UnitTypeId == null)
            .ThenBy(w => w.Position)
            .ThenBy(w => w.CreatedAt)
            .Select(w => new WaitlistCandidate(
                w.Id, w.ProspectId, w.Prospect!.Email, w.Prospect.Locale, w.Position))
            .FirstOrDefaultAsync(ct);

    public async Task MarkNotifiedAsync(Guid entryId, CancellationToken ct)
    {
        var updated = await db.WaitlistEntries
            .Where(w => w.Id == entryId && w.NotifiedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.NotifiedAt, clock.GetUtcNow())
                .SetProperty(w => w.UpdatedAt, clock.GetUtcNow()), ct);

        if (updated > 0)
        {
            logger.LogInformation("Waitlist entry {EntryId} notified", entryId);
        }
    }
}
