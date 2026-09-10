using Hubix.Domain;
using Hubix.Domain.Entities;

namespace Hubix.Infrastructure.Sales;

/// <summary>
/// What happened when someone tried to hold a unit.
/// </summary>
/// <remarks>
/// A closed set rather than exceptions: losing a race is an ordinary outcome that the page has
/// to explain calmly, not an error.
/// </remarks>
public abstract record HoldOutcome
{
    /// <summary>
    /// The unit is held. This caller won.
    /// </summary>
    /// <param name="ParkingSpaces">
    /// The space codes claimed alongside it, in order. Empty when none were asked for. The
    /// buyer is told these, because "A1" is the answer to which space is theirs.
    /// </param>
    public sealed record Created(
        Guid HoldId,
        string AccessToken,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<string> ParkingSpaces) : HoldOutcome;

    /// <summary>Somebody else got it first, or it was never available.</summary>
    public sealed record NoLongerAvailable : HoldOutcome;

    /// <summary>Hubix's serial-canceller rule: this prospect let a hold lapse.</summary>
    public sealed record ProspectBlocked(string? Reason) : HoldOutcome;

    /// <summary>Already holding as many units in this project as the rule allows.</summary>
    public sealed record TooManyHolds(int Limit) : HoldOutcome;

    public sealed record UnitNotFound : HoldOutcome;

    /// <summary>
    /// The car park ran out between the form being drawn and it being sent, or the buyer asked
    /// for more spaces than this project allows with one apartment.
    /// </summary>
    /// <remarks>
    /// Nothing is held when this comes back, the apartment included. Handing someone a flat
    /// they did not ask for on its own, because the parking they wanted had gone, is a worse
    /// answer than telling them and letting them choose again.
    /// </remarks>
    public sealed record NotEnoughParking(
        ParkingSpaceKind Kind,
        int Requested,
        int Available) : HoldOutcome;

    /// <summary>
    /// More spaces than this project allows with one apartment. Distinct from the car park
    /// being full: the spaces may well be there, but not for one buyer.
    /// </summary>
    public sealed record TooManyParkingSpaces(int Limit, int Requested) : HoldOutcome;
}

/// <summary>What a buyer asked for from the car park, if anything.</summary>
public sealed record ParkingRequest(ParkingSpaceKind Kind, int Quantity);

/// <summary>What a prospect tells us when they reserve.</summary>
public sealed record ProspectDetails(
    string Email,
    string? FirstName,
    string? Surname,
    string? Phone,
    string? City,
    string? Country,
    string Locale,
    bool ConsentGiven,
    string ConsentSource);

/// <summary>Applying what a prospect just told us to the record we already keep of them.</summary>
public static class ProspectDetailsExtensions
{
    /// <summary>
    /// Copies the details across, without wiping anything with a blank: somebody who leaves
    /// the phone box empty on their second reservation has not withdrawn their number.
    /// </summary>
    /// <remarks>
    /// <see cref="ProspectDetails.Locale"/> is deliberately not copied. A reservation typed in
    /// by staff carries the staff form's language, not the buyer's, and overwriting it here
    /// would send that buyer their next email in the wrong one. Callers that know the language
    /// came from the visitor's own browser set it themselves.
    /// </remarks>
    public static void ApplyTo(this ProspectDetails details, Prospect prospect)
    {
        prospect.FirstName = details.FirstName ?? prospect.FirstName;
        prospect.Surname = details.Surname ?? prospect.Surname;
        prospect.Phone = details.Phone ?? prospect.Phone;
        prospect.City = details.City ?? prospect.City;
        prospect.Country = details.Country ?? prospect.Country;
    }
}

/// <summary>One expired hold, and what the job did about it.</summary>
public sealed record ExpiredHold(
    Guid HoldId,
    Guid UnitId,
    string UnitCode,
    Guid ProjectId,
    string ProjectName,
    string ProjectSlug,
    Guid ProspectId,
    string ProspectEmail,
    string ProspectLocale,
    Guid? UnitTypeId);
