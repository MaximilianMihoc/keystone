using System.Text.Json;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Admin;

/// <summary>
/// Whether a prospect may hold a unit.
/// </summary>
/// <remarks>
/// <para>
/// The block itself is automatic: a hold that lapses calls <see cref="Prospect.RecordLapsedHold"/>
/// from the hold service, which is Hubix's stated rule against reservations cancelled in series.
/// Lifting it was never built, so until now a prospect who cancelled once in good faith was
/// locked out for good, and the only key was a SQL update.
/// </para>
/// <para>
/// Reads are not here. The list and the detail are projections the API does in one query each,
/// which is how every other list in this system works; a service earns its keep for the write,
/// because the write has to carry an audit row with it.
/// </para>
/// </remarks>
public sealed class ProspectAdminService(
    HubixDbContext db,
    ILogger<ProspectAdminService> logger)
{
    /// <summary>
    /// Blocks a prospect from holding, or lets them hold again.
    /// </summary>
    /// <param name="reason">Why they are being blocked. Required to block, ignored to unblock.</param>
    public async Task<AdminWriteResult> SetBlockedAsync(
        Guid prospectId,
        bool blocked,
        string? reason,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var prospect = await db.Prospects.FirstOrDefaultAsync(p => p.Id == prospectId, ct);

        if (prospect is null)
        {
            return new AdminWriteResult.NotFound();
        }

        var why = reason?.Trim() ?? "";

        // Unblocking needs no reason — the audit row already records who did it and when. A
        // block does, because the prospect is told it at the point they are refused, and
        // "you may not reserve" with no sentence after it is not something staff can defend.
        if (blocked && why.Length == 0)
        {
            return new AdminWriteResult.Rejected("Say why they are being blocked.");
        }

        // Re-blocking an already-blocked prospect with a different reason is a real edit: the
        // automatic sentence is replaced by staff's own, and that is worth an audit row.
        var alreadyThere = prospect.CanHold != blocked
                           && (!blocked || prospect.BlockReason == why);

        if (alreadyThere)
        {
            return new AdminWriteResult.NoChange();
        }

        var before = new Dictionary<string, object?>
        {
            ["CanHold"] = prospect.CanHold,
            ["BlockReason"] = prospect.BlockReason
        };

        if (blocked)
        {
            prospect.Block(why);
        }
        else
        {
            prospect.ClearBlock();
        }

        var diff = new Dictionary<string, object?>
        {
            ["CanHold"] = prospect.CanHold,
            ["BlockReason"] = prospect.BlockReason
        };

        // "prospect" is the entity type PrivacyService already audits under, so these land in
        // the same feed as an erasure when somebody asks what was done to this person.
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "prospect",
            EntityId = prospect.Id,
            Action = blocked ? "prospect.block.applied" : "prospect.block.cleared",
            Before = JsonSerializer.Serialize(before),
            After = JsonSerializer.Serialize(diff)
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            blocked
                ? "{Actor} blocked prospect {ProspectId} from holding"
                : "{Actor} let prospect {ProspectId} hold again",
            actorLabel, prospect.Id);

        return new AdminWriteResult.Updated(diff);
    }
}
