using System.Text.Json;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Admin;

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
    decimal? ParkingPriceNet,
    decimal? ParkingAdvanceNet,

    // The name of a ParkingSpaceKind, not the enum: no string-enum converter is registered,
    // so System.Text.Json would refuse "Underground" and demand an ordinal instead.
    string? ParkingSpaceKind,

    string? AdminNotes);

public abstract record UnitEditResult
{
    public sealed record Updated(IReadOnlyDictionary<string, object?> Changes) : UnitEditResult;

    public sealed record NoChange : UnitEditResult;

    public sealed record NotFound : UnitEditResult;

    public sealed record Rejected(string Reason) : UnitEditResult;
}

public sealed class UnitAdminService(
    HubixDbContext db,
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

        if (edit.UnitTypeId is { } typeId)
        {
            var typeBelongs = await db.UnitTypes
                .AnyAsync(t => t.Id == typeId && t.ProjectId == unit.ProjectId, ct);

            if (!typeBelongs)
            {
                return new UnitEditResult.Rejected("That apartment type belongs to another project.");
            }
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

        // Validated before anything is written, so a typo in the request changes nothing.
        // Null is "not sent" and an empty string clears, exactly like the text fields above.
        var spaceKindSent = edit.ParkingSpaceKind is not null;
        ParkingSpaceKind? spaceKind = null;

        if (edit.ParkingSpaceKind is { Length: > 0 } rawSpaceKind)
        {
            if (!Enum.TryParse(rawSpaceKind, out ParkingSpaceKind parsedSpaceKind)
                || !Enum.IsDefined(parsedSpaceKind))
            {
                return new UnitEditResult.Rejected(
                    $"\"{rawSpaceKind}\" is not a kind of parking space.");
            }

            spaceKind = parsedSpaceKind;
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
        ApplyVal("ParkingPriceNet", unit.ParkingPriceNet, edit.ParkingPriceNet, v => unit.ParkingPriceNet = v);
        ApplyVal("ParkingAdvanceNet", unit.ParkingAdvanceNet, edit.ParkingAdvanceNet, v => unit.ParkingAdvanceNet = v);
        // Not ApplyVal: that would record the ordinal, and an audit row has to still mean the
        // same thing after somebody reorders the enum.
        if (spaceKindSent && spaceKind != unit.ParkingSpaceKind)
        {
            before["ParkingSpaceKind"] = unit.ParkingSpaceKind?.ToString();
            changes["ParkingSpaceKind"] = spaceKind?.ToString();
            unit.ParkingSpaceKind = spaceKind;
        }

        // Availability is a consequence of there being a price, the same rule the importer
        // owns, so it is derived rather than entered and is absent from UnitEdit. It is still
        // recorded when it moves: an unaudited change is exactly what this system refuses.
        var parkingAvailable = unit.ParkingPriceNet is > 0;
        if (parkingAvailable != unit.ParkingAvailable)
        {
            before["ParkingAvailable"] = unit.ParkingAvailable;
            changes["ParkingAvailable"] = parkingAvailable;
            unit.ParkingAvailable = parkingAvailable;
        }

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
}
