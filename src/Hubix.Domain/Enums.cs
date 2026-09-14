namespace Hubix.Domain;

/// <summary>
/// Lifecycle of a project, from internal draft to archived.
/// </summary>
public enum ProjectStatus
{
    Draft,
    Upcoming,
    Selling,
    SoldOut,
    Delivered,
    Archived
}

/// <summary>
/// How the HUB is intended to be let. Drives the yield model Hubix publishes.
/// </summary>
public enum RentalProfile
{
    ShortTerm,
    MidTerm,
    LongTerm,
    Mixed
}

/// <summary>
/// The kind of parking space a price buys. A closed list rather than free text: the
/// vocabulary is small, and this way the description translates through the resource
/// files like every other label, instead of needing a translation row per unit.
/// </summary>
public enum ParkingSpaceKind
{
    AboveGroundUncovered,
    AboveGroundCovered,
    Underground
}

/// <summary>
/// The unit lifecycle. Transitions are governed by <see cref="UnitStateMachine"/>.
/// </summary>
public enum UnitState
{
    /// <summary>Listed publicly and reservable.</summary>
    Available,

    /// <summary>A prospect holds it while they decide. Expires automatically.</summary>
    Held,

    /// <summary>Off the market while a sale runs. ReservationPipeline carries how far along.</summary>
    Reserved,

    /// <summary>Cesiune signed at the notary.</summary>
    Contracted,

    /// <summary>Balance settled.</summary>
    Sold,

    /// <summary>Pulled from sale by Hubix. Delisted without being deleted.</summary>
    Withdrawn
}

/// <summary>
/// Why a hold ended. Distinguishes a prospect who lapsed from one released by staff,
/// because only the former counts against the prospect.
/// </summary>
public enum HoldReleaseReason
{
    /// <summary>Countdown ran out.</summary>
    Expired,

    /// <summary>Prospect withdrew before the countdown ended.</summary>
    Cancelled,

    /// <summary>Became a reservation.</summary>
    Converted,

    /// <summary>Released by staff. Carries no penalty; staff error is not the prospect's fault.</summary>
    Admin
}

/// <summary>
/// The sales pipeline. These are the columns Hubix keeps by hand in hidden spreadsheet
/// columns today, and they are expected to be wrong in at least one respect, since the
/// model was inferred from documents rather than observed.
/// </summary>
public enum ReservationState
{
    PendingFee,
    FeePaid,
    AgreementSent,
    AgreementSigned,
    NotaryScheduled,
    CesiuneSigned,
    Completed,
    Cancelled
}

/// <summary>
/// Determines which VAT rate applies, so it cannot be resolved from the unit alone.
/// </summary>
public enum BuyerType
{
    Individual,
    Company
}

/// <summary>
/// How a tranche's apartment portion is calculated.
/// </summary>
public enum TrancheBasis
{
    /// <summary>The project's advance percentage of the base price.</summary>
    AdvancePercentage,

    /// <summary>An explicit percentage of the base price.</summary>
    Percentage,

    /// <summary>Whatever is left after the preceding tranches.</summary>
    Remainder
}

/// <summary>
/// When a tranche falls due.
/// </summary>
public enum TrancheDue
{
    AtSigning,
    Interim,
    AtDelivery
}

public enum AssetKind
{
    FloorPlan,
    Rlv,
    Render,
    SitePlan,
    Prospectus,
    Brochure,

    /// <summary>A picture of the project, played in the slideshow on its page.</summary>
    Gallery,
    Other
}

public enum StaffRole
{
    Admin,
    Sales,
    Viewer
}
