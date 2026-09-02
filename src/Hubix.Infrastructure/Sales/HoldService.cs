using System.Text.Json;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Sales;

/// <summary>
/// Creating, releasing and expiring holds.
/// </summary>
/// <remarks>
/// This is the only place a unit becomes <see cref="UnitState.Held"/> or returns from it. Today
/// Hubix prevents double-booking by a member of staff being quick with a spreadsheet; here it is
/// a row lock.
/// </remarks>
public sealed class HoldService(
    HubixDbContext db,
    TimeProvider clock,
    ILogger<HoldService> logger)
{
    /// <summary>
    /// How many units one person may hold at once in a single project.
    /// </summary>
    /// <remarks>
    /// Hubix's documents say a buyer may take "one (or more) apartments", so holding several is
    /// legitimate. They do not say how many, and without a cap one person can lock the entire
    /// remaining inventory for two days. Three is a guess and needs confirming with Hubix.
    /// </remarks>
    public const int MaxActiveHoldsPerProject = 3;

    public async Task<HoldOutcome> TryHoldAsync(
        Guid unitId,
        ProspectDetails details,
        string? ipAddress,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var unit = await db.Units
            .AsNoTracking()
            .Where(u => u.Id == unitId)
            .Select(u => new
            {
                u.Id,
                u.Code,
                u.ProjectId,
                u.State,
                HoldHours = u.Project!.HoldDurationHours
            })
            .FirstOrDefaultAsync(ct);

        if (unit is null)
        {
            return new HoldOutcome.UnitNotFound();
        }

        // A cheap pre-check so the common "already sold" case never opens a transaction.
        // It is not the guard — the guard is the conditional UPDATE below.
        if (unit.State != UnitState.Available)
        {
            return new HoldOutcome.NoLongerAvailable();
        }

        var prospect = await FindOrCreateProspectAsync(details, ct);

        if (!prospect.CanHold)
        {
            logger.LogInformation(
                "Hold refused for blocked prospect {ProspectId} on unit {UnitCode}",
                prospect.Id, unit.Code);

            return new HoldOutcome.ProspectBlocked(prospect.BlockReason);
        }

        var activeHolds = await db.Holds
            .CountAsync(h => h.ProspectId == prospect.Id
                             && h.ReleasedAt == null
                             && h.Unit!.ProjectId == unit.ProjectId, ct);

        if (activeHolds >= MaxActiveHoldsPerProject)
        {
            return new HoldOutcome.TooManyHolds(MaxActiveHoldsPerProject);
        }

        var prospectId = prospect.Id;
        var prospectEmail = prospect.Email;
        var expiresAt = now.AddHours(unit.HoldHours);

        // The connection retries on transient failure, so the whole transaction has to be
        // replayable as one unit. Resuming a half-applied transaction after a retry is
        // exactly how a unit ends up Held with no hold row behind it.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync<HoldOutcome>(async () =>
        {
            // Each attempt starts clean, so a retry cannot insert a second copy of rows
            // the previous attempt already tracked as Added.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // ------------------------------------------------------------------------
            // The guard. Two prospects clicking Reserve on the same apartment in the
            // same second both reach this line; exactly one gets rows == 1.
            //
            // The UPDATE takes an exclusive row lock. The second transaction blocks,
            // then re-evaluates its predicate against the committed row, sees Held, and
            // matches nothing. No entity is loaded first, so there is no read-then-write
            // window to lose the race in.
            // ------------------------------------------------------------------------
            var rows = await db.Units
                .Where(u => u.Id == unitId && u.State == UnitState.Available)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.State, UnitState.Held)
                    .SetProperty(u => u.StateChangedAt, now)
                    .SetProperty(u => u.UpdatedAt, now), ct);

            if (rows == 0)
            {
                await transaction.RollbackAsync(ct);

                logger.LogInformation(
                    "Lost the race for unit {UnitCode}; prospect {ProspectId} was a moment late",
                    unit.Code, prospectId);

                return new HoldOutcome.NoLongerAvailable();
            }

            var hold = new Hold
            {
                UnitId = unitId,
                ProspectId = prospectId,
                AccessToken = Hold.NewAccessToken(),
                ExpiresAt = expiresAt
            };

            db.Holds.Add(hold);

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = "prospect",
                ActorId = prospectId,
                ActorLabel = prospectEmail,
                EntityType = "unit",
                EntityId = unitId,
                Action = "hold.created",
                Before = Json(new { State = nameof(UnitState.Available) }),
                After = Json(new { State = nameof(UnitState.Held), hold.ExpiresAt }),
                IpAddress = ipAddress
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Unit {UnitCode} held by prospect {ProspectId} until {ExpiresAt:u}",
                unit.Code, prospectId, hold.ExpiresAt);

            return new HoldOutcome.Created(hold.Id, hold.AccessToken, hold.ExpiresAt);
        });
    }

    /// <summary>
    /// Releases a hold and returns the unit to sale.
    /// </summary>
    /// <returns>False when the hold was already released; the caller can treat that as done.</returns>
    public async Task<bool> ReleaseAsync(
        Guid holdId,
        HoldReleaseReason reason,
        string? actorLabel,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // Reload inside the attempt: a retry must see committed state, not whatever the
            // previous attempt left in the tracker.
            db.ChangeTracker.Clear();

            var hold = await db.Holds
                .Include(h => h.Prospect)
                .FirstOrDefaultAsync(h => h.Id == holdId && h.ReleasedAt == null, ct);

            if (hold is null)
            {
                // Already released. The expiry job can see the same hold twice, so this is
                // an ordinary outcome, not a failure.
                return false;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            hold.Release(reason, now);

            // Converted holds stay Held; the reservation moves the unit on from there.
            if (reason is not HoldReleaseReason.Converted)
            {
                await db.Units
                    .Where(u => u.Id == hold.UnitId && u.State == UnitState.Held)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(u => u.State, UnitState.Available)
                        .SetProperty(u => u.StateChangedAt, now)
                        .SetProperty(u => u.UpdatedAt, now), ct);
            }

            // Only the prospect's own lapse counts against them. A staff release does not:
            // somebody else's mistake is not their fault.
            if (Hold.CountsAsLapse(reason) && hold.Prospect is not null)
            {
                hold.Prospect.RecordLapsedHold();
            }

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = reason is HoldReleaseReason.Expired ? "system" : "prospect",
                ActorId = hold.ProspectId,
                ActorLabel = actorLabel ?? hold.Prospect?.Email,
                EntityType = "hold",
                EntityId = hold.Id,
                Action = $"hold.released.{reason}".ToLowerInvariant(),
                After = Json(new { Reason = reason.ToString(), ReleasedAt = now })
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return true;
        });
    }

    /// <summary>
    /// Releases every hold whose countdown has run out.
    /// </summary>
    /// <remarks>
    /// Idempotent by construction: the filter only matches live holds, so a second run in the
    /// same minute — which Hangfire can do if a node is slow — finds nothing and does nothing.
    /// </remarks>
    public async Task<IReadOnlyList<ExpiredHold>> ExpireDueHoldsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var due = await db.Holds
            .Where(h => h.ReleasedAt == null && h.ExpiresAt <= now)
            .Select(h => new ExpiredHold(
                h.Id,
                h.UnitId,
                h.Unit!.Code,
                h.Unit.ProjectId,
                h.Unit.Project!.Name,
                h.Unit.Project.Slug,
                h.ProspectId,
                h.Prospect!.Email,
                h.Prospect.Locale,
                h.Unit.UnitTypeId))
            .ToListAsync(ct);

        var released = new List<ExpiredHold>(due.Count);

        foreach (var candidate in due)
        {
            if (await ReleaseAsync(candidate.HoldId, HoldReleaseReason.Expired, "system", ct))
            {
                released.Add(candidate);
            }
        }

        if (released.Count > 0)
        {
            logger.LogInformation("Expired {Count} hold(s)", released.Count);
        }

        return released;
    }

    public Task<Hold?> FindByTokenAsync(string token, CancellationToken ct) =>
        db.Holds
            .AsNoTracking()
            .Include(h => h.Unit!).ThenInclude(u => u.Project)
            .Include(h => h.Unit!).ThenInclude(u => u.UnitType!).ThenInclude(t => t.Translations)
            .Include(h => h.Prospect)
            .FirstOrDefaultAsync(h => h.AccessToken == token, ct);

    private async Task<Prospect> FindOrCreateProspectAsync(ProspectDetails details, CancellationToken ct)
    {
        var email = details.Email.Trim();

        // SQL Server's default collation is case-insensitive, so this matches regardless of
        // how they typed it and cannot create a second record for the same person.
        var prospect = await db.Prospects.FirstOrDefaultAsync(p => p.Email == email, ct);

        if (prospect is null)
        {
            prospect = new Prospect
            {
                Email = email,
                FullName = details.FullName,
                Phone = details.Phone,
                Locale = details.Locale,
                Source = "website"
            };

            db.Prospects.Add(prospect);
        }
        else
        {
            // Keep the latest details they gave us, without wiping anything with a blank.
            prospect.FullName = details.FullName ?? prospect.FullName;
            prospect.Phone = details.Phone ?? prospect.Phone;
            prospect.Locale = details.Locale;
        }

        if (details.ConsentGiven)
        {
            prospect.GdprConsentAt = clock.GetUtcNow();
            prospect.GdprConsentSource = details.ConsentSource;

            if (!prospect.GdprConsentPurpose.Contains("reservations"))
            {
                prospect.GdprConsentPurpose = [.. prospect.GdprConsentPurpose, "reservations"];
            }
        }

        await db.SaveChangesAsync(ct);
        return prospect;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
