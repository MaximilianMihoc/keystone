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
    /// The late-delivery penalty, as prose. It varies too much between projects to be a
    /// number: Coresi is 25% plus 1,000 EUR a month, Torontal is 100%, Roka is 50%.
    /// </summary>
    public string? PenaltyClause { get; set; }

    public string? MapsUrl { get; set; }

    public string? SummaryRo { get; set; }

    public string? BodyRo { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public ICollection<PricingRule> PricingRules { get; set; } = [];

    public ICollection<UnitType> UnitTypes { get; set; } = [];

    public ICollection<Floor> Floors { get; set; } = [];

    public ICollection<Unit> Units { get; set; } = [];

    public ICollection<Asset> Assets { get; set; } = [];

    public bool IsPublished => PublishedAt is not null;
}
