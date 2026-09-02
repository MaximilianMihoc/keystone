using Hubix.Domain.Common;
using Hubix.Domain.Pricing;

namespace Hubix.Domain.Entities;

/// <summary>
/// A sale in progress. Replaces the hidden right-hand columns of Hubix's inventory sheets.
/// </summary>
public class Reservation : Entity
{
    public Guid UnitId { get; set; }

    public Unit? Unit { get; set; }

    public Guid ProspectId { get; set; }

    public Prospect? Prospect { get; set; }

    public ReservationState State { get; set; } = ReservationState.PendingFee;

    /// <summary>
    /// When it arrived at its current stage.
    /// </summary>
    /// <remarks>
    /// The board's real question is not which stage a sale is at but how long it has been sat
    /// there. A reservation that has waited three weeks for a signature is the one worth
    /// chasing, and today nothing records that at all.
    /// </remarks>
    public DateTimeOffset StateChangedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- The pipeline, as Hubix tracks it today ---------------------------------
    // Ordering, and what each of these means, live in ReservationPipeline. These are just
    // the dates.

    /// <summary>"Numar contract"</summary>
    public string? ContractNumber { get; set; }

    /// <summary>"Platit rezervare"</summary>
    public DateTimeOffset? FeePaidAt { get; set; }

    /// <summary>No column of their own for this; the agreement goes out by email.</summary>
    public DateTimeOffset? AgreementSentAt { get; set; }

    /// <summary>"Trimis rezervarea semnata"</summary>
    public DateTimeOffset? SignedAgreementAt { get; set; }

    /// <summary>"Cesiune semnata la NOTAR"</summary>
    public DateTimeOffset? CesiuneSignedAt { get; set; }

    /// <summary>Balance settled and the sale closed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>"Locatie cesiune"</summary>
    public string? NotaryLocation { get; set; }

    public DateTimeOffset? NotaryAppointmentAt { get; set; }

    /// <summary>Determines which VAT rate applies, so it belongs here and not on the unit.</summary>
    public BuyerType BuyerType { get; set; } = BuyerType.Individual;

    // --- Price snapshot ---------------------------------------------------------
    // What applied when this was agreed. Hubix raises prices mid-launch — the Lake Home
    // document describes exactly that — and an agreed reservation must not move with them.
    // These are never recalculated.

    public decimal SnapBasePriceNet { get; set; }

    public decimal SnapCesiuneNet { get; set; }

    public decimal SnapVatRate { get; set; }

    public decimal SnapCesiuneVatRate { get; set; }

    /// <summary>The tranche schedule as agreed, stored as JSON.</summary>
    public IReadOnlyList<TrancheAmount> SnapTranches { get; set; } = [];

    /// <summary>
    /// The reservation fee as it stood on the day. Projects charge 1,000, 4,000 or 5,000 EUR
    /// and a project may change its mind; what this buyer was asked for must not change with it.
    /// </summary>
    public decimal? SnapReservationFee { get; set; }

    /// <summary>Was "Comments Istratescu".</summary>
    public string? InternalNotes { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public string? CancellationReason { get; set; }

    public bool IsOpen => ReservationPipeline.IsOpen(State);

    /// <summary>Freezes the agreed price onto this reservation.</summary>
    public void ApplySnapshot(PriceBreakdown breakdown)
    {
        ArgumentNullException.ThrowIfNull(breakdown);

        SnapBasePriceNet = breakdown.BasePriceNet;
        SnapCesiuneNet = breakdown.CesiuneNet;
        SnapVatRate = breakdown.VatRate;
        SnapCesiuneVatRate = breakdown.CesiuneVatRate;
        SnapTranches = breakdown.Tranches;
    }

    public decimal SnapTotalGross => SnapTranches.Sum(t => t.Gross);
}

/// <summary>
/// A place in the queue for a unit type. First come, first served, as Hubix's documents state.
/// </summary>
public class WaitlistEntry : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>Null means any type.</summary>
    public Guid? UnitTypeId { get; set; }

    public UnitType? UnitType { get; set; }

    public Guid ProspectId { get; set; }

    public Prospect? Prospect { get; set; }

    /// <summary>
    /// Arrival number within this queue, assigned once and never revised.
    /// </summary>
    /// <remarks>
    /// Not the place in the queue that anybody is shown. People ahead drop out, so a stored
    /// place goes stale the moment it is written and would tell somebody they are fifth long
    /// after they became second. What is displayed is counted at the time of asking; this is
    /// only the arrival order that counting sorts by.
    /// </remarks>
    public int Position { get; set; }

    /// <summary>
    /// Lets someone see their place and leave the queue without an account, and without
    /// having to ask Hubix to take them off by email.
    /// </summary>
    public required string AccessToken { get; set; }

    public DateTimeOffset? NotifiedAt { get; set; }

    public DateTimeOffset? ConvertedAt { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    public bool IsActive => RemovedAt is null;

    /// <summary>Whether they are still waiting, as opposed to already told about a unit.</summary>
    public bool IsWaiting => RemovedAt is null && NotifiedAt is null;
}

/// <summary>Replaces the "trimite intrebari" form.</summary>
public class Enquiry : Entity
{
    public Guid? ProjectId { get; set; }

    public Project? Project { get; set; }

    public Guid? ProspectId { get; set; }

    public Prospect? Prospect { get; set; }

    public required string Message { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    public string? AnsweredBy { get; set; }
}
