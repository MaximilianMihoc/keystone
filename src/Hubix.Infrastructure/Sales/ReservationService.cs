using System.Text.Json;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Sales;

/// <summary>
/// Starting reservations and moving them through the pipeline.
/// </summary>
/// <remarks>
/// The only place a unit reaches <see cref="UnitState.Reserved"/> with a buyer behind it, and
/// the only one that goes beyond. A unit can also be <em>marked</em> reserved with no record —
/// by the importer from a sheet, or by staff recording a sale made elsewhere — and
/// <see cref="CreateAsync"/> takes exactly that unit as its starting point. Where the
/// pipeline's shape lives in <see cref="ReservationPipeline"/>, the consequences live here: the
/// unit follows the reservation, the price is frozen on the day it is agreed, and every move
/// writes an audit row.
/// </remarks>
public sealed class ReservationService(
    HubixDbContext db,
    TimeProvider clock,
    ILogger<ReservationService> logger)
{
    /// <summary>
    /// Starts a reservation on a unit.
    /// </summary>
    /// <param name="unitId">
    /// A unit that is <see cref="UnitState.Held"/>, whose hold then converts, or one already
    /// marked <see cref="UnitState.Reserved"/> with no record behind it. The second case is not
    /// a curiosity: every project Hubix migrates arrives with sales already in flight, and a
    /// unit the spreadsheet calls reserved has to be able to become a reservation here.
    /// </param>
    /// <param name="details">
    /// The buyer. Ignored when the unit is held, since the hold already knows who they are, and
    /// required when it is not.
    /// </param>
    public async Task<ReservationOutcome> CreateAsync(
        Guid unitId,
        ProspectDetails? details,
        BuyerType buyerType,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var unit = await db.Units
            .AsNoTracking()
            .Include(u => u.Project!).ThenInclude(p => p.PricingRules)
            .FirstOrDefaultAsync(u => u.Id == unitId, ct);

        if (unit?.Project is null)
        {
            return new ReservationOutcome.NotFound();
        }

        if (unit.State is not (UnitState.Held or UnitState.Reserved))
        {
            return new ReservationOutcome.Refused(
                $"{unit.Code} is {unit.State.ToString().ToLowerInvariant()}. A reservation " +
                "starts from a hold, or from a unit already marked reserved.");
        }

        // Completed counts as taken: a sold unit must never acquire a second reservation.
        // The filtered unique index enforces the same thing; this is so the ordinary case
        // gets a sentence rather than a constraint violation.
        var taken = await db.Reservations
            .AnyAsync(r => r.UnitId == unitId && r.State != ReservationState.Cancelled, ct);

        if (taken)
        {
            return new ReservationOutcome.Refused($"{unit.Code} already has a reservation.");
        }

        var hold = await db.Holds
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.UnitId == unitId && h.ReleasedAt == null, ct);

        var prospect = hold is not null
            ? await db.Prospects.FirstOrDefaultAsync(p => p.Id == hold.ProspectId, ct)
            : details is not null
                ? await RecordBuyerAsync(details, ct)
                : null;

        if (prospect is null)
        {
            return new ReservationOutcome.Refused(
                "Nobody is holding this unit, so the buyer's email address is needed.");
        }

        var rule = unit.Project.PricingRules
            .Where(r => r.EffectiveFrom <= now)
            .MaxBy(r => r.EffectiveFrom);

        if (rule is null || rule.Tranches.Count == 0)
        {
            return new ReservationOutcome.Refused(
                $"{unit.Project.Name} has no payment schedule, so there is nothing to agree to.");
        }

        var cesiune = unit.CesiunePriceNet ?? 0m;

        if (cesiune > unit.BasePriceNet)
        {
            return new ReservationOutcome.Refused(
                $"{unit.Code} has a cesiune price above its total price. Correct the unit first.");
        }

        var breakdown = TrancheCalculator.Calculate(
            rule, unit.BasePriceNet, cesiune, ReducedVatApplies(unit, buyerType));

        var startState = unit.State;
        var prospectId = prospect.Id;
        var prospectEmail = prospect.Email;
        var reservationFee = unit.Project.ReservationFee;

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync<ReservationOutcome>(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // The guard, as in the hold service. When the unit is held this also settles the
            // race between two people converting the same hold: exactly one gets rows == 1.
            var rows = await db.Units
                .Where(u => u.Id == unitId && u.State == startState)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.State, UnitState.Reserved)
                    .SetProperty(u => u.StateChangedAt, now)
                    .SetProperty(u => u.UpdatedAt, now), ct);

            if (rows == 0)
            {
                await transaction.RollbackAsync(ct);

                return new ReservationOutcome.Refused(
                    $"{unit.Code} changed while this was being saved. Reload and try again.");
            }

            var reservation = new Reservation
            {
                UnitId = unitId,
                ProspectId = prospectId,
                BuyerType = buyerType,
                State = ReservationState.PendingFee,
                StateChangedAt = now,
                SnapReservationFee = reservationFee
            };

            reservation.ApplySnapshot(breakdown);

            // Parking claimed under the hold comes with the apartment. It is snapshotted here
            // for the reason the rest of the money is: a project may reprice its car park, and
            // what this buyer agreed to must not move with it.
            var parkingSpaceCount = 0;
            var parkingPrices = new Dictionary<Guid, (decimal Price, decimal Deposit)>();

            if (hold is not null)
            {
                var spaces = await db.ParkingSpaces
                    .Where(s => s.HoldId == hold.Id && s.State == UnitState.Held)
                    .Select(s => new { s.Id, s.Kind })
                    .ToListAsync(ct);

                parkingSpaceCount = spaces.Count;

                if (spaces.Count > 0)
                {
                    var prices = await db.ParkingPrices
                        .Where(p => p.ProjectId == unit.ProjectId)
                        .ToDictionaryAsync(p => p.Kind, ct);

                    foreach (var space in spaces)
                    {
                        if (!prices.TryGetValue(space.Kind, out var price))
                        {
                            await transaction.RollbackAsync(ct);

                            return new ReservationOutcome.Refused(
                                $"{unit.Project.Name} has no price for a {space.Kind} space, " +
                                "so there is nothing to agree to. Set one first.");
                        }

                        // Kept per space as well as summed. Dropping one of these later has to
                        // subtract what that space cost, which the total alone cannot say once
                        // the car park has been repriced.
                        parkingPrices[space.Id] = (price.PriceNet, price.DepositNet);

                        reservation.SnapParkingNet += price.PriceNet;
                        reservation.SnapParkingDepositNet += price.DepositNet;
                    }
                }
            }

            db.Reservations.Add(reservation);

            if (hold is not null)
            {
                // Converted, not released: the unit stays off the market, because it has just
                // moved further along rather than come back.
                await db.Holds
                    .Where(h => h.Id == hold.Id && h.ReleasedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(h => h.ReleasedAt, now)
                        .SetProperty(h => h.ReleaseReason, HoldReleaseReason.Converted)
                        .SetProperty(h => h.UpdatedAt, now), ct);
            }

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = "staff",
                ActorId = actorId,
                ActorLabel = actorLabel,
                EntityType = "reservation",
                EntityId = reservation.Id,
                Action = "reservation.created",
                Before = Json(new { unit.Code, State = startState.ToString() }),
                After = Json(new
                {
                    unit.Code,
                    State = nameof(UnitState.Reserved),
                    Buyer = prospectEmail,
                    Stage = nameof(ReservationState.PendingFee),
                    breakdown.TotalGross,
                    FromHold = hold is not null
                })
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two people recording a reservation on the same already-reserved unit. The
                // guarded UPDATE cannot separate them, because both saw Reserved and both set
                // Reserved; UX_Reservations_OneOpenPerUnit is what stops the second. Anything
                // else is a real fault and is rethrown.
                await transaction.RollbackAsync(ct);

                if (!await HasOpenReservationAsync(unitId, ct))
                {
                    throw;
                }

                return new ReservationOutcome.Refused($"{unit.Code} already has a reservation.");
            }

            // After the save, not before: the spaces point at the reservation row by foreign
            // key, and it does not exist until here.
            if (hold is not null)
            {
                var moved = 0;

                // One statement per space rather than one for all of them: each carries the
                // price agreed for its own kind, and a single UPDATE cannot say two things.
                foreach (var (spaceId, agreed) in parkingPrices)
                {
                    moved += await db.ParkingSpaces
                        .Where(s => s.Id == spaceId
                                    && s.HoldId == hold.Id
                                    && s.State == UnitState.Held)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(p => p.State, UnitState.Reserved)
                            .SetProperty(p => p.ReservationId, reservation.Id)
                            .SetProperty(p => p.HoldId, (Guid?)null)
                            .SetProperty(p => p.SnapPriceNet, agreed.Price)
                            .SetProperty(p => p.SnapDepositNet, agreed.Deposit)
                            .SetProperty(p => p.StateChangedAt, now)
                            .SetProperty(p => p.UpdatedAt, now), ct);
                }

                // The price was summed from a read taken earlier in this transaction, and the
                // expiry job could have released the hold in between. Fewer spaces here than
                // were priced means the snapshot describes spaces the buyer has not got.
                if (moved != parkingSpaceCount)
                {
                    await transaction.RollbackAsync(ct);

                    return new ReservationOutcome.Refused(
                        $"The parking held with {unit.Code} changed while this was being saved. " +
                        "Reload and try again.");
                }
            }

            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "{Actor} started a reservation on {UnitCode} for {Buyer}",
                actorLabel, unit.Code, prospectEmail);

            return new ReservationOutcome.Created(reservation.Id, reservation.State);
        });
    }

    /// <summary>
    /// Moves a reservation to another stage, taking the unit with it.
    /// </summary>
    /// <param name="cancellationReason">Required when moving to Cancelled, ignored otherwise.</param>
    public async Task<ReservationOutcome> MoveAsync(
        Guid reservationId,
        ReservationState target,
        string? cancellationReason,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync<ReservationOutcome>(async () =>
        {
            db.ChangeTracker.Clear();

            var reservation = await db.Reservations
                .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

            if (reservation is null)
            {
                return new ReservationOutcome.NotFound();
            }

            if (reservation.State == target)
            {
                return new ReservationOutcome.NoChange();
            }

            if (ReservationPipeline.Refuse(reservation, target) is { } refusal)
            {
                return new ReservationOutcome.Refused(refusal);
            }

            var unit = await db.Units
                .AsNoTracking()
                .Where(u => u.Id == reservation.UnitId)
                .Select(u => new { u.Code, u.State })
                .FirstAsync(ct);

            var path = ReservationPipeline.UnitPath(unit.State, reservation.State, target);

            // Check the whole path before changing anything, so a move that cannot finish
            // does not half-happen.
            var walked = unit.State;
            foreach (var next in path)
            {
                if (!UnitStateMachine.CanTransition(walked, next))
                {
                    return new ReservationOutcome.Refused(
                        $"{unit.Code} cannot go from {walked.ToString().ToLowerInvariant()} to " +
                        $"{next.ToString().ToLowerInvariant()}.");
                }

                walked = next;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var at = unit.State;
            foreach (var next in path)
            {
                var expected = at;

                var rows = await db.Units
                    .Where(u => u.Id == reservation.UnitId && u.State == expected)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(u => u.State, next)
                        .SetProperty(u => u.StateChangedAt, now)
                        .SetProperty(u => u.UpdatedAt, now), ct);

                if (rows == 0)
                {
                    await transaction.RollbackAsync(ct);

                    return new ReservationOutcome.Refused(
                        $"{unit.Code} is no longer {expected.ToString().ToLowerInvariant()}. " +
                        "Reload and try again.");
                }

                at = next;
            }

            // Parking follows the apartment through every stage, cancellation included. A
            // cancelled sale that left its spaces Reserved would put them beyond reach of every
            // release path there is, and the unique code means they could not even be recreated.
            var spaceState = at;
            var releasing = spaceState is UnitState.Available;

            await db.ParkingSpaces
                .Where(s => s.ReservationId == reservation.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.State, spaceState)
                    .SetProperty(p => p.ReservationId, releasing ? null : (Guid?)reservation.Id)
                    .SetProperty(p => p.StateChangedAt, now)
                    .SetProperty(p => p.UpdatedAt, now), ct);

            var from = reservation.State;
            StampDates(reservation, target, cancellationReason, now);

            reservation.State = target;
            reservation.StateChangedAt = now;

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = "staff",
                ActorId = actorId,
                ActorLabel = actorLabel,
                EntityType = "reservation",
                EntityId = reservation.Id,
                Action = target == ReservationState.Cancelled
                    ? "reservation.cancelled"
                    : "reservation.moved",
                Before = Json(new { Stage = from.ToString(), Unit = unit.State.ToString() }),
                After = Json(new
                {
                    Stage = target.ToString(),
                    Unit = at.ToString(),
                    Reason = cancellationReason
                })
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The reservation changed between the read at the top and here — most likely
                // somebody cancelled it. The rowversion is what makes this a refusal the board
                // can render rather than a silent overwrite of their work (issue #36).
                await transaction.RollbackAsync(ct);

                return new ReservationOutcome.Refused(
                    $"The reservation on {unit.Code} changed while this was being saved. " +
                    "Reload and try again.");
            }

            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "{Actor} moved {UnitCode} from {From} to {To}",
                actorLabel, unit.Code, from, target);

            return new ReservationOutcome.Moved(from, target, at, reservation.UnitId);
        });
    }

    /// <summary>Edits the paperwork fields. Stages move through <see cref="MoveAsync"/>.</summary>
    public async Task<ReservationOutcome> EditAsync(
        Guid reservationId,
        ReservationEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var reservation = await db.Reservations
            .Include(r => r.Unit!).ThenInclude(u => u.Project!).ThenInclude(p => p.PricingRules)
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

        if (reservation is null)
        {
            return new ReservationOutcome.NotFound();
        }

        if (reservation.State == ReservationState.Cancelled)
        {
            return new ReservationOutcome.Refused("This reservation was cancelled.");
        }

        var changes = new Dictionary<string, object?>();
        var before = new Dictionary<string, object?>();

        void ApplyRef<T>(string name, T? current, T? incoming, Action<T?> set) where T : class
        {
            if (incoming is null || EqualityComparer<T?>.Default.Equals(current, incoming))
            {
                return;
            }

            before[name] = current;
            changes[name] = incoming;
            set(incoming);
        }

        void ApplyVal<T>(string name, T? current, T? incoming, Action<T> set) where T : struct
        {
            if (incoming is null || Nullable.Equals(current, incoming))
            {
                return;
            }

            before[name] = current;
            changes[name] = incoming;
            set(incoming.Value);
        }

        ApplyRef("ContractNumber", reservation.ContractNumber, edit.ContractNumber,
            v => reservation.ContractNumber = v);
        ApplyRef("NotaryLocation", reservation.NotaryLocation, edit.NotaryLocation,
            v => reservation.NotaryLocation = v);
        ApplyRef("InternalNotes", reservation.InternalNotes, edit.InternalNotes,
            v => reservation.InternalNotes = v);
        ApplyVal("NotaryAppointmentAt", reservation.NotaryAppointmentAt, edit.NotaryAppointmentAt,
            v => reservation.NotaryAppointmentAt = v);

        // Buyer type decides the VAT rate, and the VAT rate is inside the frozen price. Changing
        // it later would leave the snapshot describing a schedule this buyer was never offered,
        // so it is only editable while nothing has been agreed — and then the price is redone.
        if (edit.BuyerType is { } buyerType && buyerType != reservation.BuyerType)
        {
            if (reservation.State != ReservationState.PendingFee)
            {
                return new ReservationOutcome.Refused(
                    "The buyer type sets the VAT rate, which is part of the agreed price. " +
                    "It can only be changed before the reservation fee is paid.");
            }

            if (!TryResnapshot(reservation, buyerType, out var reason))
            {
                return new ReservationOutcome.Refused(reason);
            }

            before["BuyerType"] = reservation.BuyerType.ToString();
            changes["BuyerType"] = buyerType.ToString();
            changes["SnapTotalGross"] = reservation.SnapTotalGross;
            reservation.BuyerType = buyerType;
        }

        if (changes.Count == 0)
        {
            return new ReservationOutcome.NoChange();
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "reservation",
            EntityId = reservation.Id,
            Action = "reservation.edited",
            Before = Json(before),
            After = Json(changes)
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Same window as a move: this reads, decides, and only then writes. An edit landing
            // on top of a cancellation would restore the row as open (issue #36).
            return new ReservationOutcome.Refused(
                "This reservation changed while your edit was open. Reload and try again.");
        }

        return new ReservationOutcome.Updated(changes);
    }

    /// <summary>
    /// Whether the reduced rate applies to this buyer and this unit.
    /// </summary>
    /// <remarks>
    /// Eligibility is a fact about the unit; it only takes effect for a private buyer. That is
    /// how the reduced rate works in Romania as far as the documents show, and it is one of the
    /// things worth putting to Hubix directly.
    /// </remarks>
    private static bool ReducedVatApplies(Unit unit, BuyerType buyerType) =>
        unit.ReducedVatEligible && buyerType == BuyerType.Individual;

    private bool TryResnapshot(Reservation reservation, BuyerType buyerType, out string reason)
    {
        var unit = reservation.Unit;
        var rule = unit?.Project?.PricingRules
            .Where(r => r.EffectiveFrom <= clock.GetUtcNow())
            .MaxBy(r => r.EffectiveFrom);

        if (unit is null || rule is null || rule.Tranches.Count == 0)
        {
            reason = "This project has no payment schedule, so the price cannot be recalculated.";
            return false;
        }

        reservation.ApplySnapshot(TrancheCalculator.Calculate(
            rule,
            unit.BasePriceNet,
            unit.CesiunePriceNet ?? 0m,
            ReducedVatApplies(unit, buyerType)));

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Stamps or clears the dates a move implies.
    /// </summary>
    /// <remarks>
    /// Forward stamps only the stage arrived at. Stages jumped over keep their empty dates,
    /// which is the truth: nobody knows when they happened. A step back clears the date of the
    /// stage being left, because that stage did not happen after all.
    /// </remarks>
    private static void StampDates(
        Reservation reservation,
        ReservationState target,
        string? cancellationReason,
        DateTimeOffset now)
    {
        if (target == ReservationState.Cancelled)
        {
            reservation.CancelledAt = now;
            reservation.CancellationReason = cancellationReason;
            return;
        }

        var forward = ReservationPipeline.IndexOf(target) > ReservationPipeline.IndexOf(reservation.State);

        if (forward)
        {
            var stage = ReservationPipeline.For(target);

            if (stage.SetDate is not null && stage.GetDate?.Invoke(reservation) is null)
            {
                stage.SetDate(reservation, now);
            }

            return;
        }

        ReservationPipeline.For(reservation.State).SetDate?.Invoke(reservation, null);
    }

    /// <summary>
    /// Records a buyer staff entered by hand.
    /// </summary>
    /// <remarks>
    /// Separate from the hold service's version on purpose: somebody typed in by a member of
    /// staff has given no consent through any form here, so none is claimed on their behalf.
    /// </remarks>
    private async Task<Prospect> RecordBuyerAsync(ProspectDetails details, CancellationToken ct)
    {
        var email = details.Email.Trim();

        var prospect = await db.Prospects.FirstOrDefaultAsync(p => p.Email == email, ct);

        if (prospect is null)
        {
            prospect = new Prospect
            {
                Email = email,
                Locale = details.Locale,
                Source = "staff"
            };

            details.ApplyTo(prospect);
            db.Prospects.Add(prospect);
        }
        else
        {
            details.ApplyTo(prospect);
        }

        await db.SaveChangesAsync(ct);
        return prospect;
    }

    private Task<bool> HasOpenReservationAsync(Guid unitId, CancellationToken ct) =>
        db.Reservations
            .AsNoTracking()
            .AnyAsync(r => r.UnitId == unitId && r.State != ReservationState.Cancelled, ct);

    /// <summary>
    /// The last stage at which what was bought can still change.
    /// </summary>
    /// <remarks>
    /// Up to the notary signing, nothing legally binding names a space. The cesiune does, so
    /// after it a quiet admin edit would leave the record disagreeing with the deed.
    /// </remarks>
    private static bool ParkingCanChange(ReservationState state) =>
        state is ReservationState.PendingFee
            or ReservationState.FeePaid
            or ReservationState.AgreementSent
            or ReservationState.AgreementSigned
            or ReservationState.NotaryScheduled;

    /// <summary>
    /// Puts a free space on an open reservation, at today's price.
    /// </summary>
    /// <remarks>
    /// The buyer is agreeing to it now, so it takes the price now, and that price is written
    /// onto the space rather than only added to the reservation's total — removing it later has
    /// to subtract what this space actually cost.
    /// </remarks>
    public async Task<ReservationOutcome> AddParkingAsync(
        Guid reservationId,
        Guid spaceId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var reservation = await db.Reservations
            .Include(r => r.Unit)
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

        if (reservation is null)
        {
            return new ReservationOutcome.NotFound();
        }

        if (!ParkingCanChange(reservation.State))
        {
            return new ReservationOutcome.Refused(
                $"This sale has reached {reservation.State}. Parking can only change up to " +
                "the notary signing, because the cesiune names the space.");
        }

        var space = await db.ParkingSpaces
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == spaceId, ct);

        if (space is null)
        {
            return new ReservationOutcome.NotFound();
        }

        if (space.ProjectId != reservation.Unit!.ProjectId)
        {
            return new ReservationOutcome.Refused($"{space.Code} belongs to another development.");
        }

        var price = await db.ParkingPrices
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProjectId == space.ProjectId && p.Kind == space.Kind, ct);

        if (price is null)
        {
            return new ReservationOutcome.Refused(
                $"There is no price for a {space.Kind} space, so there is nothing to agree to.");
        }

        var priceNet = price.PriceNet;
        var depositNet = price.DepositNet;
        var target = ReservationPipeline.For(reservation.State).Unit;

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync<ReservationOutcome>(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // The same guard that claims a space for a hold. Sales and a visitor can reach for
            // the same free bay in the same second, and exactly one of them may have it.
            var rows = await db.ParkingSpaces
                .Where(s => s.Id == spaceId && s.State == UnitState.Available)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.State, target)
                    .SetProperty(p => p.ReservationId, reservationId)
                    .SetProperty(p => p.SnapPriceNet, priceNet)
                    .SetProperty(p => p.SnapDepositNet, depositNet)
                    .SetProperty(p => p.StateChangedAt, now)
                    .SetProperty(p => p.UpdatedAt, now), ct);

            if (rows == 0)
            {
                await transaction.RollbackAsync(ct);

                return new ReservationOutcome.Refused(
                    $"{space.Code} is no longer free. Reload and pick another.");
            }

            await db.Reservations
                .Where(r => r.Id == reservationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.SnapParkingNet, r => r.SnapParkingNet + priceNet)
                    .SetProperty(r => r.SnapParkingDepositNet,
                        r => r.SnapParkingDepositNet + depositNet)
                    .SetProperty(r => r.UpdatedAt, now), ct);

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = "staff",
                ActorId = actorId,
                ActorLabel = actorLabel,
                EntityType = "reservation",
                EntityId = reservationId,
                Action = "reservation.parking.added",
                After = Json(new
                {
                    space.Code,
                    Kind = space.Kind.ToString(),
                    PriceNet = priceNet,
                    DepositNet = depositNet
                })
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "{Actor} added parking {Code} to the reservation on {UnitCode}",
                actorLabel, space.Code, reservation.Unit.Code);

            return new ReservationOutcome.Updated(new Dictionary<string, object?>
            {
                ["ParkingAdded"] = space.Code,
                ["SnapParkingNet"] = priceNet
            });
        });
    }

    /// <summary>
    /// Takes a space off an open reservation and returns it to the market.
    /// </summary>
    /// <remarks>
    /// The reservation's total loses exactly what this space cost, read from the space itself.
    /// Subtracting a share of the summed figure would be wrong the moment the car park has been
    /// repriced since.
    /// </remarks>
    public async Task<ReservationOutcome> RemoveParkingAsync(
        Guid reservationId,
        Guid spaceId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var reservation = await db.Reservations
            .Include(r => r.Unit)
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

        if (reservation is null)
        {
            return new ReservationOutcome.NotFound();
        }

        if (!ParkingCanChange(reservation.State))
        {
            return new ReservationOutcome.Refused(
                $"This sale has reached {reservation.State}. Parking can only change up to " +
                "the notary signing, because the cesiune names the space.");
        }

        var space = await db.ParkingSpaces
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == spaceId && s.ReservationId == reservationId, ct);

        if (space is null)
        {
            return new ReservationOutcome.NotFound();
        }

        // What this space contributed. Null means it joined before prices were kept per space;
        // taking nothing off is better than taking off a guess.
        var priceNet = space.SnapPriceNet ?? 0m;
        var depositNet = space.SnapDepositNet ?? 0m;
        var code = space.Code;

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync<ReservationOutcome>(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var rows = await db.ParkingSpaces
                .Where(s => s.Id == spaceId && s.ReservationId == reservationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.State, UnitState.Available)
                    .SetProperty(p => p.ReservationId, (Guid?)null)
                    .SetProperty(p => p.SnapPriceNet, (decimal?)null)
                    .SetProperty(p => p.SnapDepositNet, (decimal?)null)
                    .SetProperty(p => p.StateChangedAt, now)
                    .SetProperty(p => p.UpdatedAt, now), ct);

            if (rows == 0)
            {
                await transaction.RollbackAsync(ct);

                return new ReservationOutcome.Refused(
                    $"{code} is no longer on this reservation. Reload and try again.");
            }

            await db.Reservations
                .Where(r => r.Id == reservationId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.SnapParkingNet, r => r.SnapParkingNet - priceNet)
                    .SetProperty(r => r.SnapParkingDepositNet,
                        r => r.SnapParkingDepositNet - depositNet)
                    .SetProperty(r => r.UpdatedAt, now), ct);

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = "staff",
                ActorId = actorId,
                ActorLabel = actorLabel,
                EntityType = "reservation",
                EntityId = reservationId,
                Action = "reservation.parking.removed",
                Before = Json(new { Code = code, PriceNet = priceNet, DepositNet = depositNet })
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "{Actor} removed parking {Code} from the reservation on {UnitCode}",
                actorLabel, code, reservation.Unit!.Code);

            return new ReservationOutcome.Updated(new Dictionary<string, object?>
            {
                ["ParkingRemoved"] = code,
                ["SnapParkingNet"] = -priceNet
            });
        });
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
