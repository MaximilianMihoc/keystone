using Hubix.Domain.Common;
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
/// Replaces a separate Google Form per apartment type, which is why entries carry the type
/// they are waiting for rather than living in separate lists. Somebody who will take anything
/// joins the same queue with no type at all.
/// </remarks>
public sealed class WaitlistService(
    HubixDbContext db,
    TimeProvider clock,
    ILogger<WaitlistService> logger)
{
    /// <summary>
    /// Puts somebody on the list, or gives them back the place they already had.
    /// </summary>
    /// <param name="unitTypeId">Null means they will take any apartment in the project.</param>
    public async Task<WaitlistOutcome> JoinAsync(
        Guid projectId,
        Guid? unitTypeId,
        ProspectDetails details,
        CancellationToken ct)
    {
        var project = await db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId && p.PublishedAt != null, ct);

        if (!project)
        {
            return new WaitlistOutcome.ProjectNotFound();
        }

        if (unitTypeId is { } typeId
            && !await db.UnitTypes.AnyAsync(t => t.Id == typeId && t.ProjectId == projectId, ct))
        {
            return new WaitlistOutcome.UnknownType();
        }

        var prospect = await RegisterAsync(details, ct);

        var existing = await db.WaitlistEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.ProjectId == projectId
                                      && w.UnitTypeId == unitTypeId
                                      && w.ProspectId == prospect.Id
                                      && w.RemovedAt == null, ct);

        if (existing is not null)
        {
            // Submitting the form twice is a misclick, not an offence.
            return new WaitlistOutcome.AlreadyWaiting(
                existing.Id, existing.AccessToken, await PlaceOfAsync(existing.Id, ct));
        }

        // Arrival number. Two people joining in the same instant can be given the same one;
        // that is harmless, because every query orders by CreatedAt after this, so the queue
        // still has one definite order. It is not worth a lock.
        var arrived = await db.WaitlistEntries
            .Where(w => w.ProjectId == projectId && w.UnitTypeId == unitTypeId)
            .CountAsync(ct);

        var entry = new WaitlistEntry
        {
            ProjectId = projectId,
            UnitTypeId = unitTypeId,
            ProspectId = prospect.Id,
            Position = arrived + 1,
            AccessToken = Hubix.Domain.Common.AccessToken.New()
        };

        db.WaitlistEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Email} joined the waiting list for project {ProjectId}", prospect.Email, projectId);

        return new WaitlistOutcome.Joined(
            entry.Id, entry.AccessToken, await PlaceOfAsync(entry.Id, ct));
    }

    /// <summary>Takes somebody off the list. Idempotent, so a second click is harmless.</summary>
    /// <returns>False when there was nothing active to remove.</returns>
    public async Task<bool> LeaveAsync(string token, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var removed = await db.WaitlistEntries
            .Where(w => w.AccessToken == token && w.RemovedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RemovedAt, now)
                .SetProperty(w => w.UpdatedAt, now), ct);

        if (removed > 0)
        {
            logger.LogInformation("A waiting list entry was removed by its own link");
        }

        return removed > 0;
    }

    public async Task<WaitlistPlace?> FindByTokenAsync(string token, CancellationToken ct)
    {
        var entry = await db.WaitlistEntries
            .AsNoTracking()
            .Where(w => w.AccessToken == token)
            .Select(w => new
            {
                w.Id,
                w.ProjectId,
                w.UnitTypeId,
                ProjectName = w.Project!.Name,
                ProjectSlug = w.Project.Slug,
                TypeName = w.UnitType == null ? null : w.UnitType.Name,
                w.CreatedAt,
                w.NotifiedAt,
                w.RemovedAt
            })
            .FirstOrDefaultAsync(ct);

        if (entry is null)
        {
            return null;
        }

        return new WaitlistPlace(
            entry.Id,
            entry.ProjectName,
            entry.ProjectSlug,
            entry.TypeName,
            await PlaceOfAsync(entry.Id, ct),
            await QueueLengthAsync(entry.ProjectId, entry.UnitTypeId, ct),
            entry.CreatedAt,
            entry.NotifiedAt,
            entry.RemovedAt);
    }

    /// <summary>
    /// Where somebody stands in their own queue, counted now.
    /// </summary>
    /// <remarks>
    /// Counted rather than stored, so it falls as people ahead drop out or are notified. Only
    /// their own queue is counted: telling somebody waiting for a studio that they are behind
    /// everybody waiting for a three-bedroom would be true of the matching order and useless
    /// as information.
    /// </remarks>
    public async Task<int> PlaceOfAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await db.WaitlistEntries
            .AsNoTracking()
            .Where(w => w.Id == entryId)
            .Select(w => new { w.ProjectId, w.UnitTypeId, w.Position, w.CreatedAt, w.RemovedAt })
            .FirstOrDefaultAsync(ct);

        if (entry is null || entry.RemovedAt is not null)
        {
            return 0;
        }

        var ahead = await db.WaitlistEntries
            .CountAsync(w => w.ProjectId == entry.ProjectId
                             && w.UnitTypeId == entry.UnitTypeId
                             && w.RemovedAt == null
                             && w.NotifiedAt == null
                             && (w.Position < entry.Position
                                 || (w.Position == entry.Position && w.CreatedAt < entry.CreatedAt)), ct);

        return ahead + 1;
    }

    public Task<int> QueueLengthAsync(Guid projectId, Guid? unitTypeId, CancellationToken ct) =>
        db.WaitlistEntries
            .CountAsync(w => w.ProjectId == projectId
                             && w.UnitTypeId == unitTypeId
                             && w.RemovedAt == null
                             && w.NotifiedAt == null, ct);

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

    /// <summary>Takes somebody off the list on their behalf. Staff action, and audited.</summary>
    public async Task<bool> RemoveAsync(
        Guid entryId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var removed = await db.WaitlistEntries
            .Where(w => w.Id == entryId && w.RemovedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RemovedAt, now)
                .SetProperty(w => w.UpdatedAt, now), ct);

        if (removed == 0)
        {
            return false;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "waitlist",
            EntityId = entryId,
            Action = "waitlist.removed"
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Records somebody who signed up through the website.
    /// </summary>
    /// <remarks>
    /// Consent is captured for the waiting list specifically, not folded into whatever they
    /// may have agreed to when reserving. The Equity document promises the prospect database
    /// is used only for its stated purpose, and "stated purpose" has to mean this one.
    /// </remarks>
    private async Task<Prospect> RegisterAsync(ProspectDetails details, CancellationToken ct)
    {
        var email = details.Email.Trim();

        var prospect = await db.Prospects.FirstOrDefaultAsync(p => p.Email == email, ct);

        if (prospect is null)
        {
            prospect = new Prospect
            {
                Email = email,
                FullName = details.FullName,
                Phone = details.Phone,
                Locale = details.Locale,
                Source = "waitlist"
            };

            db.Prospects.Add(prospect);
        }
        else
        {
            prospect.FullName = details.FullName ?? prospect.FullName;
            prospect.Phone = details.Phone ?? prospect.Phone;
            prospect.Locale = details.Locale;
        }

        if (details.ConsentGiven)
        {
            prospect.GdprConsentAt = clock.GetUtcNow();
            prospect.GdprConsentSource = details.ConsentSource;

            if (!prospect.GdprConsentPurpose.Contains("waitlist"))
            {
                prospect.GdprConsentPurpose = [.. prospect.GdprConsentPurpose, "waitlist"];
            }
        }

        await db.SaveChangesAsync(ct);
        return prospect;
    }
}
