using System.Text.Json;
using Keystone.Domain;
using Keystone.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keystone.Infrastructure.Admin;

/// <summary>
/// The fields the units grid may change.
/// </summary>
/// <remarks>
/// Notably absent: <c>State</c>. A unit's state moves through the state machine and the hold
/// service, never by someone typing in a grid — that is exactly the discipline the spreadsheet
/// lacks, where availability is a text cell anyone can overwrite.
/// </remarks>
public sealed record UnitEdit(
    string? Building,
    string? Stair,
    int? FloorLevel,
    Guid? UnitTypeId,
    string? Orientation,
    decimal? AreaBuilt,
    decimal? AreaUsable,
    decimal? AreaBalcony,
    decimal? BasePriceNet,
    decimal? CesiunePriceNet,
    bool? ReducedVatEligible,
    string? AdminNotes);

/// <summary>
/// A single apartment, entered by hand.
/// </summary>
/// <remarks>
/// The four required fields are exactly the importer's required columns, so an apartment added
/// here and one arriving by spreadsheet need the same things to exist.
/// </remarks>
public sealed record UnitCreate(
    string Code,
    Guid UnitTypeId,
    decimal AreaUsable,
    decimal BasePriceNet,
    int? Crt = null,
    string? Building = null,
    string? Stair = null,
    int? FloorLevel = null,
    string? Orientation = null,
    decimal? AreaBuilt = null,
    decimal? AreaBalcony = null,
    decimal? CesiunePriceNet = null,
    bool ReducedVatEligible = false,
    string? AdminNotes = null);

public abstract record UnitEditResult
{
    public sealed record Updated(IReadOnlyDictionary<string, object?> Changes) : UnitEditResult;

    public sealed record NoChange : UnitEditResult;

    public sealed record NotFound : UnitEditResult;

    public sealed record Rejected(string Reason) : UnitEditResult;
}

public sealed class UnitAdminService(
    KeystoneDbContext db,
    TimeProvider clock,
    ILogger<UnitAdminService> logger)
{
    public async Task<UnitEditResult> EditAsync(
        Guid unitId,
        UnitEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct);

        if (unit is null)
        {
            return new UnitEditResult.NotFound();
        }

        // Editing the price of a unit somebody has already agreed to buy would be silently
        // rewriting their contract. Reservations snapshot their own prices, so history is
        // safe either way, but the edit is still refused: it means the grid and the signed
        // paperwork now disagree, and somebody should know.
        if (unit.State is UnitState.Contracted or UnitState.Sold
            && (edit.BasePriceNet is not null || edit.CesiunePriceNet is not null))
        {
            return new UnitEditResult.Rejected(
                $"{unit.Code} is {unit.State.ToString().ToLowerInvariant()}. " +
                "Its price cannot be changed here.");
        }

        if (edit.UnitTypeId is { } typeId
            && !await db.UnitTypes.AnyAsync(t => t.Id == typeId, ct))
        {
            return new UnitEditResult.Rejected("That apartment type does not exist.");
        }

        var changes = new Dictionary<string, object?>();
        var before = new Dictionary<string, object?>();

        // A null in the payload means "not sent", not "clear this". That is the ordinary
        // PATCH bargain, and it means a numeric field cannot be blanked through the grid —
        // acceptable, since none of them should be empty on a real unit. Text fields clear
        // by sending an empty string, which is not null.
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

        ApplyRef("Building", unit.Building, edit.Building, v => unit.Building = v);
        ApplyRef("Stair", unit.Stair, edit.Stair, v => unit.Stair = v);
        ApplyRef("Orientation", unit.Orientation, edit.Orientation, v => unit.Orientation = v);
        ApplyRef("AdminNotes", unit.AdminNotes, edit.AdminNotes, v => unit.AdminNotes = v);

        ApplyVal("FloorLevel", unit.FloorLevel, edit.FloorLevel, v => unit.FloorLevel = v);
        ApplyVal("UnitTypeId", unit.UnitTypeId, edit.UnitTypeId, v => unit.UnitTypeId = v);
        ApplyVal("AreaBuilt", unit.AreaBuilt, edit.AreaBuilt, v => unit.AreaBuilt = v);
        ApplyVal("AreaUsable", unit.AreaUsable, edit.AreaUsable, v => unit.AreaUsable = v);
        ApplyVal("AreaBalcony", unit.AreaBalcony, edit.AreaBalcony, v => unit.AreaBalcony = v);
        ApplyVal("BasePriceNet", unit.BasePriceNet, edit.BasePriceNet, v => unit.BasePriceNet = v);
        ApplyVal("CesiunePriceNet", unit.CesiunePriceNet, edit.CesiunePriceNet, v => unit.CesiunePriceNet = v);
        ApplyVal("ReducedVatEligible", unit.ReducedVatEligible, edit.ReducedVatEligible, v => unit.ReducedVatEligible = v);

        if (changes.Count == 0)
        {
            return new UnitEditResult.NoChange();
        }

        if (unit.AreaUsable <= 0)
        {
            return new UnitEditResult.Rejected("Usable area must be greater than zero.");
        }

        if (unit.BasePriceNet < 0)
        {
            return new UnitEditResult.Rejected("Price cannot be negative.");
        }

        if (unit.CesiunePriceNet > unit.BasePriceNet)
        {
            // The assignment fee is Hubix's margin taken out of what the buyer pays, not
            // added on top, so it cannot exceed the total.
            return new UnitEditResult.Rejected(
                "The cesiune price cannot exceed the total price; it is part of it.");
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "unit",
            EntityId = unit.Id,
            Action = "unit.edited",
            Before = JsonSerializer.Serialize(before),
            After = JsonSerializer.Serialize(changes)
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Actor} edited {Fields} on unit {UnitId}",
            actorLabel, string.Join(", ", changes.Keys), unit.Id);

        return new UnitEditResult.Updated(changes);
    }

    /// <summary>
    /// Takes a unit off sale, or puts it back.
    /// </summary>
    /// <remarks>
    /// The one state change the grid may make, and it still goes through the state machine.
    /// Withdrawing a unit somebody is holding would strand them, so it is refused.
    /// </remarks>
    public async Task<UnitEditResult> SetWithdrawnAsync(
        Guid unitId,
        bool withdrawn,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct);

        if (unit is null)
        {
            return new UnitEditResult.NotFound();
        }

        var target = withdrawn ? UnitState.Withdrawn : UnitState.Available;

        if (unit.State == target)
        {
            return new UnitEditResult.NoChange();
        }

        if (!UnitStateMachine.CanTransition(unit.State, target))
        {
            return new UnitEditResult.Rejected(
                $"{unit.Code} is {unit.State.ToString().ToLowerInvariant()} and cannot be " +
                $"{(withdrawn ? "withdrawn" : "put back on sale")} from there.");
        }

        var now = clock.GetUtcNow();
        var previous = unit.State;

        unit.State = target;
        unit.StateChangedAt = now;

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "unit",
            EntityId = unit.Id,
            Action = "unit.state_changed",
            Before = JsonSerializer.Serialize(new { State = previous.ToString() }),
            After = JsonSerializer.Serialize(new { State = target.ToString() })
        });

        await db.SaveChangesAsync(ct);

        return new UnitEditResult.Updated(
            new Dictionary<string, object?> { ["State"] = target.ToString() });
    }

    /// <summary>
    /// Marks a unit reserved for a sale that happened somewhere else, or takes that mark off
    /// again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This records a fact, not a reservation. Hubix sell through agents and through
    /// conversations that never touch this system, and a unit somebody has already agreed to
    /// buy must be able to stop being offered without a buyer, a fee and a pipeline card
    /// invented to justify it. <c>ReservationService.CreateAsync</c> then accepts exactly this
    /// unit — reserved, no record — so the pipeline can be started on it later, when the
    /// details are known.
    /// </para>
    /// <para>
    /// Taking the mark off is refused once a reservation exists, because from that moment the
    /// pipeline owns the unit's state and cancelling there is what returns it to sale.
    /// </para>
    /// </remarks>
    public async Task<UnitEditResult> SetReservedAsync(
        Guid unitId,
        bool reserved,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct);

        if (unit is null)
        {
            return new UnitEditResult.NotFound();
        }

        var target = reserved ? UnitState.Reserved : UnitState.Available;

        if (unit.State == target)
        {
            return new UnitEditResult.NoChange();
        }

        var booked = await db.Reservations
            .AnyAsync(r => r.UnitId == unitId && r.State != ReservationState.Cancelled, ct);

        if (booked)
        {
            return new UnitEditResult.Rejected(
                $"{unit.Code} has a reservation behind it. Move it on the pipeline instead.");
        }

        // The state machine allows Held -> Reserved because that is how a hold converts, and
        // converting is the reservation service's job: it has a prospect to carry across. A
        // mark here would take the apartment from under them and leave the hold dangling.
        if (reserved && unit.State is not (UnitState.Available or UnitState.Withdrawn))
        {
            return new UnitEditResult.Rejected(
                $"{unit.Code} is {unit.State.ToString().ToLowerInvariant()}. Convert the hold " +
                "on the pipeline instead.");
        }

        if (!UnitStateMachine.CanTransition(unit.State, target))
        {
            return new UnitEditResult.Rejected(
                $"{unit.Code} is {unit.State.ToString().ToLowerInvariant()} and cannot be " +
                $"{(reserved ? "marked reserved" : "put back on sale")} from there.");
        }

        var now = clock.GetUtcNow();
        var previous = unit.State;

        unit.State = target;
        unit.StateChangedAt = now;

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "unit",
            EntityId = unit.Id,
            Action = "unit.state_changed",
            Before = JsonSerializer.Serialize(new { State = previous.ToString() }),
            After = JsonSerializer.Serialize(new { State = target.ToString(), Reason = "marked by staff, no reservation" })
        });

        await db.SaveChangesAsync(ct);

        return new UnitEditResult.Updated(
            new Dictionary<string, object?> { ["State"] = target.ToString() });
    }

    /// <summary>
    /// Adds a single apartment by hand.
    /// </summary>
    /// <remarks>
    /// The spreadsheet importer remains the way a project's inventory arrives; this is for the
    /// one that was missed, or the floor that got added late. It therefore asks for exactly what
    /// the importer requires — a code, a type, a usable area and a price — and leaves the rest
    /// optional, so the two doors into the same table cannot disagree about what a unit needs.
    /// <para>
    /// A new apartment starts <c>Available</c>. State belongs to the hold and reservation
    /// services, and there is no honest way for a creation form to claim otherwise.
    /// </para>
    /// </remarks>
    public async Task<AdminWriteResult> CreateAsync(
        Guid projectId,
        UnitCreate create,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var code = create.Code.Trim();

        if (code.Length == 0)
        {
            return new AdminWriteResult.Rejected("An apartment needs a code.");
        }

        if (!await db.UnitTypes.AnyAsync(t => t.Id == create.UnitTypeId, ct))
        {
            return new AdminWriteResult.Rejected("That apartment type does not exist.");
        }

        if (Unreasonable(create.AreaUsable, create.BasePriceNet, create.CesiunePriceNet) is { } problem)
        {
            return new AdminWriteResult.Rejected(problem);
        }

        if (await db.Units.AnyAsync(u => u.ProjectId == projectId && u.Code == code, ct))
        {
            return new AdminWriteResult.Rejected(Taken(code));
        }

        var unit = new Unit
        {
            ProjectId = projectId,
            Code = code,
            Crt = create.Crt,
            UnitTypeId = create.UnitTypeId,
            Building = Blank(create.Building),
            Stair = Blank(create.Stair),
            FloorLevel = create.FloorLevel,
            Orientation = Blank(create.Orientation),
            AreaBuilt = create.AreaBuilt,
            AreaUsable = create.AreaUsable,
            AreaBalcony = create.AreaBalcony ?? 0m,
            BasePriceNet = create.BasePriceNet,
            CesiunePriceNet = create.CesiunePriceNet,
            ReducedVatEligible = create.ReducedVatEligible,
            AdminNotes = Blank(create.AdminNotes),
            State = UnitState.Available,
            StateChangedAt = clock.GetUtcNow()
        };

        db.Units.Add(unit);

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "unit",
            EntityId = unit.Id,
            Action = "unit.created",
            After = JsonSerializer.Serialize(new { unit.Code, unit.AreaUsable, unit.BasePriceNet })
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            // The check above can lose a race with an import running at the same time. Re-ask
            // rather than assume: a different failure reported as a duplicate code would send
            // somebody looking in the wrong place entirely.
            if (!await db.Units.AnyAsync(u => u.ProjectId == projectId && u.Code == code, ct))
            {
                throw;
            }

            return new AdminWriteResult.Rejected(Taken(code));
        }

        logger.LogInformation("{Actor} added apartment {Code} to project {ProjectId}",
            actorLabel, code, projectId);

        return new AdminWriteResult.Created(unit.Id);
    }

    /// <summary>
    /// Removes an apartment nobody has ever wanted.
    /// </summary>
    /// <remarks>
    /// Never one with a hold or a reservation behind it, cancelled ones included. Those rows are
    /// the record of what happened, and <c>Hold → Unit</c> cascades, so deleting the apartment
    /// would take that record with it. Withdrawing is the way to take a real apartment off sale.
    /// </remarks>
    public async Task<AdminWriteResult> DeleteAsync(
        Guid unitId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct);

        if (unit is null)
        {
            return new AdminWriteResult.NotFound();
        }

        if (unit.State is not (UnitState.Available or UnitState.Withdrawn))
        {
            return new AdminWriteResult.Rejected(
                $"{unit.Code} is {unit.State.ToString().ToLowerInvariant()}, so it cannot be deleted.");
        }

        if (await db.Reservations.AnyAsync(r => r.UnitId == unitId, ct))
        {
            return new AdminWriteResult.Rejected(
                $"{unit.Code} has been reserved before, and that history would go with it. " +
                "Withdraw it instead.");
        }

        if (await db.Holds.AnyAsync(h => h.UnitId == unitId, ct))
        {
            return new AdminWriteResult.Rejected(
                $"Somebody has held {unit.Code} before, and that history would go with it. " +
                "Withdraw it instead.");
        }

        db.Units.Remove(unit);

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "unit",
            EntityId = unitId,
            Action = "unit.deleted",
            Before = JsonSerializer.Serialize(new { unit.Code, unit.BasePriceNet })
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Actor} deleted apartment {Code}", actorLabel, unit.Code);

        return new AdminWriteResult.Deleted();
    }

    /// <summary>
    /// Corrects a mistyped apartment code.
    /// </summary>
    /// <remarks>
    /// Its own method rather than another field on <see cref="UnitEdit"/>, because the code is
    /// not a property of the apartment so much as its name: the importer matches on it, the
    /// public URL contains it, and a reservation refers to it. Changing it once anybody has
    /// acted on the apartment would rewrite what they agreed to, so it is only ever allowed
    /// while nothing has happened.
    /// </remarks>
    public async Task<AdminWriteResult> RenameAsync(
        Guid unitId,
        string code,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct);

        if (unit is null)
        {
            return new AdminWriteResult.NotFound();
        }

        var wanted = code.Trim();

        if (wanted.Length == 0)
        {
            return new AdminWriteResult.Rejected("An apartment needs a code.");
        }

        if (wanted == unit.Code)
        {
            return new AdminWriteResult.NoChange();
        }

        if (unit.State is not (UnitState.Available or UnitState.Withdrawn))
        {
            return new AdminWriteResult.Rejected(
                $"{unit.Code} is {unit.State.ToString().ToLowerInvariant()}, so its code cannot change.");
        }

        if (await db.Reservations.AnyAsync(r => r.UnitId == unitId, ct)
            || await db.Holds.AnyAsync(h => h.UnitId == unitId, ct))
        {
            return new AdminWriteResult.Rejected(
                $"Somebody has already held or reserved {unit.Code}, so its code cannot change now.");
        }

        if (await db.Units.AnyAsync(u => u.ProjectId == unit.ProjectId && u.Code == wanted, ct))
        {
            return new AdminWriteResult.Rejected(Taken(wanted));
        }

        var before = unit.Code;
        unit.Code = wanted;

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "unit",
            EntityId = unitId,
            Action = "unit.renamed",
            Before = JsonSerializer.Serialize(new { Code = before }),
            After = JsonSerializer.Serialize(new { Code = wanted })
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            if (!await db.Units.AnyAsync(u => u.ProjectId == unit.ProjectId && u.Code == wanted, ct))
            {
                throw;
            }

            return new AdminWriteResult.Rejected(Taken(wanted));
        }

        return new AdminWriteResult.Updated(new Dictionary<string, object?> { ["Code"] = wanted });
    }

    private static string Taken(string code) =>
        $"There is already an apartment called {code} in this project.";

    private static string? Unreasonable(decimal areaUsable, decimal basePrice, decimal? cesiune) =>
        areaUsable <= 0 ? "Usable area must be greater than zero."
        : basePrice < 0 ? "Price cannot be negative."
        : cesiune > basePrice ? "The cesiune price cannot exceed the total price; it is part of it."
        : null;

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
