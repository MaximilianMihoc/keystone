using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>Studio, Studio Dublu, 2 camere, 3 camere. Waitlists queue by this.</summary>
/// <remarks>
/// One catalogue for the whole portal rather than a list per project: "2 camere" means the
/// same thing in every development, and a shared row is translated once instead of per project.
/// </remarks>
public class UnitType : Entity
{
    /// <summary>
    /// Canonical name, in Hubix's own Romanian. This is what the importer matches on, so it
    /// stays put; <see cref="Translations"/> only changes what a visitor reads.
    /// </summary>
    public required string Name { get; set; }

    public int SortOrder { get; set; }

    public ICollection<Unit> Units { get; set; } = [];

    public ICollection<UnitTypeTranslation> Translations { get; set; } = [];

    /// <summary>
    /// Display name for a culture, falling back to the canonical Romanian name rather than to
    /// a translation in some other language.
    /// </summary>
    public string DisplayName(string culture) =>
        Translations.ForCulture(culture, fallBackToAny: false)?.DisplayName ?? Name;
}

/// <summary>
/// A floor within a building. Exists so the floor plan is uploaded once and every unit on
/// that floor inherits it, rather than a Drive link being pasted into each spreadsheet row.
/// </summary>
public class Floor : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>Bloc, e.g. "F". Null where a project has one building.</summary>
    public string? Building { get; set; }

    public int Level { get; set; }

    /// <summary>Hubix's label, e.g. "Etaj 1".</summary>
    public string? Label { get; set; }

    public Guid? FloorPlanAssetId { get; set; }

    public Asset? FloorPlanAsset { get; set; }
}

/// <summary>
/// One apartment.
/// </summary>
/// <remarks>
/// Note what is absent: no tranche columns. Those are calculated by
/// <see cref="Pricing.TrancheCalculator"/> from the project's pricing rule, which is the fix
/// for the column drift across the source spreadsheets.
/// </remarks>
public class Unit : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>Hubix's "Nr Crt", kept so rows can be cross-referenced against their sheets.</summary>
    public int? Crt { get; set; }

    /// <summary>The apartment code, e.g. "Ap.203". Unique within a project.</summary>
    public required string Code { get; set; }

    public string? Building { get; set; }

    public string? Stair { get; set; }

    /// <summary>Numeric, so floors sort correctly. "Etaj 10" must not fall between 1 and 2.</summary>
    public int? FloorLevel { get; set; }

    public Guid UnitTypeId { get; set; }

    public UnitType? UnitType { get; set; }

    /// <summary>Compass orientation as Hubix records it: "W", "S-E".</summary>
    public string? Orientation { get; set; }

    /// <summary>MPC — built area.</summary>
    public decimal? AreaBuilt { get; set; }

    /// <summary>MPU — usable area.</summary>
    public decimal AreaUsable { get; set; }

    public decimal AreaBalcony { get; set; }

    /// <summary>
    /// Usable plus balcony. A persisted computed column, so it can never disagree with its
    /// inputs — unlike the spreadsheet formula, which some rows have lost.
    /// </summary>
    public decimal AreaTotal { get; private set; }

    /// <summary>Net price, excluding VAT.</summary>
    public decimal BasePriceNet { get; set; }

    /// <summary>The assignment price: what Hubix negotiated, excluding VAT.</summary>
    public decimal? CesiunePriceNet { get; set; }

    /// <summary>
    /// Hubix's "TVA redus?" column. Eligibility is a fact about the unit; whether it actually
    /// applies also depends on the buyer, and is resolved on the reservation.
    /// </summary>
    public bool ReducedVatEligible { get; set; }

    // Parking is not a property of an apartment: the spaces are finite, individually
    // numbered, and two buyers of two different flats compete for the same one. It lives in
    // ParkingSpace, reserved the way a unit is.

    public UnitState State { get; set; } = UnitState.Available;

    public DateTimeOffset StateChangedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? AdminNotes { get; set; }

    public ICollection<Hold> Holds { get; set; } = [];

    public ICollection<Reservation> Reservations { get; set; } = [];

    public ICollection<UnitAsset> Assets { get; set; } = [];

    public bool IsPubliclyVisible => State is not UnitState.Withdrawn;

    public bool IsReservable => State is UnitState.Available;
}
