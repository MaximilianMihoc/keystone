using System.Text.Json;
using Keystone.Domain;
using Keystone.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keystone.Infrastructure.Privacy;

/// <summary>Everything held about one person, as they are entitled to receive it.</summary>
public sealed record PersonalData(
    string Email,
    string? FullName,
    string? Phone,
    string? City,
    string? Country,
    string Locale,
    string? Source,
    DateTimeOffset FirstSeen,
    DateTimeOffset? ConsentGivenAt,
    IReadOnlyList<string> ConsentPurposes,
    string? ConsentSource,
    bool CanHold,
    string? BlockReason,
    IReadOnlyList<HoldRecord> Holds,
    IReadOnlyList<ReservationRecord> Reservations,
    IReadOnlyList<WaitlistRecord> WaitingLists,
    IReadOnlyList<EnquiryRecord> Enquiries);

public sealed record HoldRecord(
    string Project, string Unit, DateTimeOffset TakenAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? ReleasedAt, string? ReleaseReason);

public sealed record ReservationRecord(
    string Project, string Unit, string Stage, DateTimeOffset StartedAt,
    string? ContractNumber, decimal AgreedTotalGross, DateTimeOffset? CancelledAt);

public sealed record WaitlistRecord(
    string Project, string? ApartmentType, DateTimeOffset JoinedAt,
    DateTimeOffset? NotifiedAt, DateTimeOffset? RemovedAt);

public sealed record EnquiryRecord(string? Project, string Message, DateTimeOffset SentAt);

public abstract record ForgetOutcome
{
    /// <summary>Nothing was owed to anybody, so everything went.</summary>
    public sealed record Deleted : ForgetOutcome;

    /// <summary>
    /// Identity removed, records kept.
    /// </summary>
    /// <param name="Reason">Why the records had to stay, in words that can be sent to them.</param>
    public sealed record Anonymised(string Reason) : ForgetOutcome;

    public sealed record NotFound : ForgetOutcome;
}

/// <summary>
/// The right to see what is held, and the right to have it removed.
/// </summary>
/// <remarks>
/// <para>
/// Erasure is not always deletion, and pretending otherwise would be the dishonest choice.
/// Where somebody has a reservation with money against it, the record stays for the same
/// reason an invoice stays — Romanian accounting rules do not bend to a delete button — and
/// what goes instead is everything identifying them. Where nothing is owed either way, the
/// whole lot is deleted.
/// </para>
/// <para>
/// Audit rows survive both paths with their labels redacted. They are the record of what the
/// system did, and a deletion that also erased the evidence of the deletion would be worse
/// than useless.
/// </para>
/// </remarks>
public sealed class PrivacyService(
    KeystoneDbContext db,
    TimeProvider clock,
    ILogger<PrivacyService> logger)
{
    /// <summary>
    /// Records that somebody asked, so the clock can be shown to have been met.
    /// </summary>
    /// <param name="requestedAt">
    /// When the email or call arrived, which staff may be entering days later. Defaults to now,
    /// and is clamped to now, because a request from the future is a typo rather than a
    /// deadline — and a mistyped year would otherwise read as comfortably in hand forever.
    /// </param>
    public async Task<PrivacyRequest> LogRequestAsync(
        string email,
        PrivacyRequestKind kind,
        DateTimeOffset? requestedAt,
        string? note,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var request = new PrivacyRequest
        {
            Email = email.Trim(),
            Kind = kind,
            RequestedAt = requestedAt is { } asked && asked < now ? asked : now,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };

        db.PrivacyRequests.Add(request);

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "privacy-request",
            EntityId = request.Id,
            Action = "privacy.request.logged",
            After = JsonSerializer.Serialize(new
            {
                Kind = kind.ToString(),
                request.RequestedAt,
                request.DueAt
            })
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Actor} logged a {Kind} request, due {Due:u}", actorLabel, kind, request.DueAt);

        return request;
    }

    /// <summary>Everything asked by one address, newest first.</summary>
    public Task<List<PrivacyRequest>> RequestsForAsync(string email, CancellationToken ct)
    {
        var trimmed = email.Trim();

        return db.PrivacyRequests
            .AsNoTracking()
            .Where(r => r.Email == trimmed)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Everything still open, oldest first, so whatever is closest to its deadline is on top.
    /// </summary>
    public Task<List<PrivacyRequest>> OpenRequestsAsync(CancellationToken ct) =>
        db.PrivacyRequests
            .AsNoTracking()
            .Where(r => r.CompletedAt == null)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Closes one request by hand.
    /// </summary>
    /// <remarks>
    /// Answering an access request means sending somebody a file, which this system cannot
    /// witness — the export is a download, and a GET that changed data would be wrong in a
    /// way browsers and proxies punish. So staff say when it was sent. Erasure is different:
    /// <see cref="ForgetAsync"/> closes its own requests, because it is already a write and
    /// because it destroys the address the request would otherwise be found by.
    /// </remarks>
    public async Task<bool> MarkAnsweredAsync(
        Guid requestId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var request = await db.PrivacyRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.CompletedAt == null, ct);

        if (request is null)
        {
            // Already answered, or never there. Both are "nothing left to do".
            return false;
        }

        var now = clock.GetUtcNow();
        request.Complete("answered", now);

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "privacy-request",
            EntityId = request.Id,
            Action = "privacy.request.answered",
            After = JsonSerializer.Serialize(new
            {
                Kind = request.Kind.ToString(),
                request.RequestedAt,
                AnsweredAt = now,
                WithinDeadline = now <= request.DueAt
            })
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PersonalData?> ExportAsync(string email, CancellationToken ct)
    {
        var trimmed = email.Trim();

        var prospect = await db.Prospects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Email == trimmed, ct);

        if (prospect is null)
        {
            return null;
        }

        var holds = await db.Holds
            .AsNoTracking()
            .Where(h => h.ProspectId == prospect.Id)
            .OrderBy(h => h.CreatedAt)
            .Select(h => new HoldRecord(
                h.Unit!.Project!.Name, h.Unit.Code, h.CreatedAt,
                h.ExpiresAt, h.ReleasedAt,
                h.ReleaseReason == null ? null : h.ReleaseReason.ToString()))
            .ToListAsync(ct);

        var reservations = await db.Reservations
            .AsNoTracking()
            .Where(r => r.ProspectId == prospect.Id)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new
            {
                Project = r.Unit!.Project!.Name,
                Unit = r.Unit.Code,
                Stage = r.State.ToString(),
                r.CreatedAt,
                r.ContractNumber,
                r.SnapTranches,
                r.CancelledAt
            })
            .ToListAsync(ct);

        var waiting = await db.WaitlistEntries
            .AsNoTracking()
            .Where(w => w.ProspectId == prospect.Id)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new WaitlistRecord(
                w.Project!.Name,
                w.UnitType == null ? null : w.UnitType.Name,
                w.CreatedAt, w.NotifiedAt, w.RemovedAt))
            .ToListAsync(ct);

        var enquiries = await db.Enquiries
            .AsNoTracking()
            .Where(e => e.ProspectId == prospect.Id)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new EnquiryRecord(
                e.Project == null ? null : e.Project.Name, e.Message, e.CreatedAt))
            .ToListAsync(ct);

        return new PersonalData(
            prospect.Email,
            prospect.FullName,
            prospect.Phone,
            prospect.City,
            prospect.Country,
            prospect.Locale,
            prospect.Source,
            prospect.CreatedAt,
            prospect.GdprConsentAt,
            prospect.GdprConsentPurpose,
            prospect.GdprConsentSource,
            prospect.CanHold,
            prospect.BlockReason,
            holds,
            // The agreed total is summed here rather than in SQL: the schedule is a JSON
            // column and cannot be added up by the database.
            reservations.Select(r => new ReservationRecord(
                r.Project, r.Unit, r.Stage, r.CreatedAt, r.ContractNumber,
                r.SnapTranches.Sum(t => t.Gross), r.CancelledAt)).ToList(),
            waiting,
            enquiries);
    }

    /// <summary>Removes a person, keeping only what there is a legal duty to keep.</summary>
    public async Task<ForgetOutcome> ForgetAsync(
        string email,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var trimmed = email.Trim();

        var prospect = await db.Prospects.FirstOrDefaultAsync(p => p.Email == trimmed, ct);

        if (prospect is null)
        {
            return new ForgetOutcome.NotFound();
        }

        var now = clock.GetUtcNow();

        // Money having moved is what makes a record something other than marketing data.
        var mustKeep = await db.Reservations
            .AnyAsync(r => r.ProspectId == prospect.Id
                           && (r.State != ReservationState.Cancelled || r.FeePaidAt != null), ct);

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync<ForgetOutcome>(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var person = await db.Prospects.SingleAsync(p => p.Id == prospect.Id, ct);

            // An active hold keeps an apartment off the market. Erasing its owner without
            // releasing it first would strand the unit with nobody left to explain why.
            var stranded = await db.Holds
                .Where(h => h.ProspectId == person.Id && h.ReleasedAt == null)
                .Select(h => h.UnitId)
                .ToListAsync(ct);

            if (stranded.Count > 0)
            {
                await db.Holds
                    .Where(h => h.ProspectId == person.Id && h.ReleasedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(h => h.ReleasedAt, now)
                        .SetProperty(h => h.ReleaseReason, HoldReleaseReason.Admin)
                        .SetProperty(h => h.UpdatedAt, now), ct);

                await db.Units
                    .Where(u => stranded.Contains(u.Id) && u.State == UnitState.Held)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(u => u.State, UnitState.Available)
                        .SetProperty(u => u.StateChangedAt, now)
                        .SetProperty(u => u.UpdatedAt, now), ct);

                // With the apartments, and for both outcomes below: releasing the hold without
                // freeing its spaces would leave them Held against a hold that is already
                // released, which no release path can reach afterwards.
                await db.ParkingSpaces
                    .Where(s => s.Hold != null && s.Hold.ProspectId == person.Id
                                && s.State == UnitState.Held)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.State, UnitState.Available)
                        .SetProperty(p => p.HoldId, (Guid?)null)
                        .SetProperty(p => p.StateChangedAt, now)
                        .SetProperty(p => p.UpdatedAt, now), ct);
            }

            // The audit trail keeps its rows and loses its names. Erasing the evidence that
            // an erasure happened would defeat the point of having a trail.
            await db.AuditLogs
                .Where(a => a.ActorId == person.Id || a.ActorLabel == person.Email)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.ActorLabel, "[erased]")
                    .SetProperty(a => a.IpAddress, (string?)null), ct);

            ForgetOutcome outcome;

            if (mustKeep)
            {
                // Stable, unique, and no way back to a person.
                person.Email = $"erased-{person.Id:N}@removed.invalid";
                person.FirstName = null;
                person.Surname = null;
                person.Phone = null;
                person.City = null;
                person.Country = null;
                person.Source = "erased";
                person.GdprConsentAt = null;
                person.GdprConsentPurpose = [];
                person.GdprConsentSource = null;
                person.BlockReason = null;
                person.AnonymisedAt = now;

                await db.WaitlistEntries.Where(w => w.ProspectId == person.Id).ExecuteDeleteAsync(ct);
                await db.Enquiries.Where(e => e.ProspectId == person.Id).ExecuteDeleteAsync(ct);

                outcome = new ForgetOutcome.Anonymised(
                    "A reservation with money against it has to be kept for accounting. " +
                    "Everything identifying you has been removed from it.");
            }
            else
            {
                await db.WaitlistEntries.Where(w => w.ProspectId == person.Id).ExecuteDeleteAsync(ct);
                await db.Enquiries.Where(e => e.ProspectId == person.Id).ExecuteDeleteAsync(ct);

                // Spaces on the reservations about to be deleted. Holds were dealt with above,
                // for both outcomes; these only exist on the delete path. The foreign keys are
                // NoAction precisely so this has to be deliberate rather than silent.
                await db.ParkingSpaces
                    .Where(s => s.Reservation != null && s.Reservation.ProspectId == person.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.State, UnitState.Available)
                        .SetProperty(p => p.ReservationId, (Guid?)null)
                        .SetProperty(p => p.StateChangedAt, now)
                        .SetProperty(p => p.UpdatedAt, now), ct);

                await db.Reservations.Where(r => r.ProspectId == person.Id).ExecuteDeleteAsync(ct);
                await db.Holds.Where(h => h.ProspectId == person.Id).ExecuteDeleteAsync(ct);

                db.Prospects.Remove(person);

                outcome = new ForgetOutcome.Deleted();
            }

            // Close whatever they asked for and take their address off it, in this transaction
            // rather than after it. The row is the proof the request was met in time; the
            // address on it would be a copy of the person who asked not to be kept.
            await db.PrivacyRequests
                .Where(r => r.Email == trimmed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.CompletedAt, r => r.CompletedAt ?? now)
                    .SetProperty(r => r.Outcome, r => r.Outcome ?? (mustKeep ? "anonymised" : "deleted"))
                    .SetProperty(r => r.Email, (string?)null)
                    .SetProperty(r => r.UpdatedAt, now), ct);

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = "staff",
                ActorId = actorId,
                ActorLabel = actorLabel,
                EntityType = "prospect",
                EntityId = person.Id,
                Action = mustKeep ? "prospect.anonymised" : "prospect.deleted",
                After = JsonSerializer.Serialize(new
                {
                    HoldsReleased = stranded.Count,
                    Kept = mustKeep
                })
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "{Actor} {What} a prospect at their request", actorLabel,
                mustKeep ? "anonymised" : "deleted");

            return outcome;
        });
    }
}
