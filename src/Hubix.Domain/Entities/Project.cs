using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>A developer Hubix partners with.</summary>
public class Developer : Entity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public int? YearsActive { get; set; }

    public int? UnitsBuilt { get; set; }

    public string? Website { get; set; }

    public ICollection<Project> Projects { get; set; } = [];
}

/// <summary>
/// One development in which Hubix has committed to a block of apartments.
/// </summary>
/// <remarks>
/// This record is the single source of truth. The prospectus, the public inventory, the
/// reservation flow, the waitlist and the admin pipeline are all renderings of it.
/// </remarks>
public class Project : Entity
{
    /// <summary>URL segment, e.g. <c>roka-park-view</c>. Frozen once published.</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    public required string City { get; set; }

    public Guid? DeveloperId { get; set; }

    public Developer? Developer { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;

    /// <summary>
    /// Where this development sits in every list of developments, lowest first.
    /// </summary>
    /// <remarks>
    /// Set on the Organisation screen. Every list breaks ties on whatever it sorted by before,
    /// so leaving them all at zero leaves those lists exactly as they were.
    /// </remarks>
    public int SortOrder { get; set; }

    /// <summary>
    /// How many apartments Hubix committed to.
    /// </summary>
    /// <remarks>
    /// Stated exactly once. The Roka prospectus says 20 in its description and 30 in its
    /// contract section; the spreadsheet has 20. One field makes that disagreement impossible.
    /// </remarks>
    public int? UnitsCommitted { get; set; }

    public DateOnly? DeliveryDate { get; set; }

    /// <summary>Contractual grace period on delivery. Roka and Lake Home allow six months.</summary>
    public int DeliveryGraceMonths { get; set; }

    public RentalProfile? RentalProfile { get; set; }

    /// <summary>Reservation fee: 1,000 / 4,000 / 5,000 EUR depending on the project.</summary>
    public decimal? ReservationFee { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// How long a prospect holds a unit while deciding. Hubix says "1-2 days" in prose;
    /// this makes it a number the countdown can be driven from.
    /// </summary>
    public int HoldDurationHours { get; set; } = 48;

    /// <summary>
    /// How many apartments one person may hold at once in this project.
    /// </summary>
    /// <remarks>
    /// Hubix's documents say a buyer may take "one (or more) apartments", so holding several is
    /// legitimate. They do not say how many, and without a cap one person can lock the entire
    /// remaining inventory for the length of a hold. Three is a guess and needs confirming with
    /// Hubix (issue #61); it is per project so the answer can be set without a deploy.
    /// </remarks>
    public int MaxActiveHoldsPerProspect { get; set; } = 3;

    /// <summary>
    /// How many parking spaces one buyer may take with one apartment. Zero means parking is not
    /// sold alongside a flat here, which is Roka's case.
    /// </summary>
    /// <remarks>
    /// Per project rather than a constant, because the developments differ. The numbers are a
    /// guess in the same way <see cref="MaxActiveHoldsPerProspect"/> is a guess: Hubix's
    /// documents say a car park exists and what a space costs, never how many one buyer may have.
    /// </remarks>
    public int MaxParkingSpacesPerReservation { get; set; }

    public string? MapsUrl { get; set; }

    /// <summary>
    /// Where enquiries and hold notices about this development go.
    /// </summary>
    /// <remarks>
    /// Blank falls back to the portal-wide inbox. Developments are sold by different people,
    /// and an address that can only be changed by a deploy is an address that goes stale.
    /// </remarks>
    public string? SalesInbox { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// Prose, per culture. Summary, body and the penalty clause live here rather than on the
    /// project, so a third language is a data change rather than a migration.
    /// </summary>
    public ICollection<ProjectTranslation> Translations { get; set; } = [];

    public ICollection<PricingRule> PricingRules { get; set; } = [];

    public ICollection<Floor> Floors { get; set; } = [];

    public ICollection<Unit> Units { get; set; } = [];

    public ICollection<Asset> Assets { get; set; } = [];

    public ICollection<ParkingSpace> ParkingSpaces { get; set; } = [];

    public ICollection<ParkingPrice> ParkingPrices { get; set; } = [];

    public bool IsPublished => PublishedAt is not null;
}
