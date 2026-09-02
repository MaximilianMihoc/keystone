using Hubix.Domain;

namespace Hubix.Infrastructure.Sales;

/// <summary>
/// What happened to a reservation.
/// </summary>
/// <remarks>
/// A closed set rather than exceptions, for the same reason the hold service uses one: most of
/// these are ordinary answers a member of staff needs explained, not faults.
/// </remarks>
public abstract record ReservationOutcome
{
    public sealed record Created(Guid ReservationId, ReservationState State) : ReservationOutcome;

    /// <param name="UnitId">
    /// So the caller can act on what the move did to the unit. A cancellation puts an
    /// apartment back on sale, and somebody on the waiting list is owed that news.
    /// </param>
    public sealed record Moved(
        ReservationState From,
        ReservationState To,
        UnitState UnitState,
        Guid UnitId) : ReservationOutcome;

    public sealed record Updated(IReadOnlyDictionary<string, object?> Changes) : ReservationOutcome;

    public sealed record NoChange : ReservationOutcome;

    public sealed record NotFound : ReservationOutcome;

    /// <summary>The move or edit is not allowed, and this is what to tell the person.</summary>
    public sealed record Refused(string Reason) : ReservationOutcome;
}

/// <summary>
/// The fields staff may change on a reservation.
/// </summary>
/// <remarks>
/// Not here: <c>State</c>, which moves through <see cref="ReservationPipeline"/>, and the
/// snapshot prices, which are frozen by definition. Nulls mean "not sent" rather than "clear
/// this", the ordinary PATCH bargain.
/// </remarks>
public sealed record ReservationEdit(
    string? ContractNumber,
    string? NotaryLocation,
    DateTimeOffset? NotaryAppointmentAt,
    string? InternalNotes,
    BuyerType? BuyerType);
