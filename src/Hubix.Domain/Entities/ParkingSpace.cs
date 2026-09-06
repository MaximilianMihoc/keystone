using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>
/// One numbered parking space.
/// </summary>
/// <remarks>
/// Inventory in its own right rather than a column on <see cref="Unit"/>. Spaces are finite and
/// individually identified, and two buyers of two different apartments compete for the same one,
/// so a price hanging off a flat could neither say how many are left nor stop them both being
/// promised it. This mirrors <see cref="Unit"/> deliberately: a code unique within its project,
/// a state, and the same conditional-UPDATE claim.
/// </remarks>
public class ParkingSpace : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>How the space is identified on the ground: "A1", "Z3".</summary>
    public required string Code { get; set; }

    public ParkingSpaceKind Kind { get; set; }

    /// <summary>
    /// Which floor of the car park, signed: underground is negative, ground is 0, above ground
    /// is positive. Independent of <see cref="Kind"/>, which is Hubix's own vocabulary and says
    /// whether the space is covered rather than exactly where it sits.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Reuses <see cref="UnitState"/>. A space moves through exactly the states an apartment
    /// does and is governed by the same machine; a second almost-identical enum would only be a
    /// second place to get the transitions wrong.
    /// </summary>
    public UnitState State { get; set; } = UnitState.Available;

    public DateTimeOffset StateChangedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The apartment hold this space was claimed under, while that hold is live.</summary>
    public Guid? HoldId { get; set; }

    public Hold? Hold { get; set; }

    /// <summary>Set when the hold converts. This is the link to the buyer and the sale.</summary>
    public Guid? ReservationId { get; set; }

    public Reservation? Reservation { get; set; }

    public string? AdminNotes { get; set; }
}

/// <summary>
/// What a kind of space costs on one project.
/// </summary>
/// <remarks>
/// Per kind rather than per space, because that is how the developments price them: every
/// underground space on a project costs the same. Repricing is then one row rather than one per
/// bay. A space with no matching row is not for sale.
/// </remarks>
public class ParkingPrice : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public ParkingSpaceKind Kind { get; set; }

    /// <summary>Hubix's "Pret total parcare fara TVA".</summary>
    public decimal PriceNet { get; set; }

    /// <summary>Their "Avans + cesiune parcare fara TVA": what falls due at signing.</summary>
    public decimal DepositNet { get; set; }
}
