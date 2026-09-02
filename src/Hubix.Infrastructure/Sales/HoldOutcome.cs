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
    /// <summary>The unit is held. This caller won.</summary>
    public sealed record Created(Guid HoldId, string AccessToken, DateTimeOffset ExpiresAt) : HoldOutcome;

    /// <summary>Somebody else got it first, or it was never available.</summary>
    public sealed record NoLongerAvailable : HoldOutcome;

    /// <summary>Hubix's serial-canceller rule: this prospect let a hold lapse.</summary>
    public sealed record ProspectBlocked(string? Reason) : HoldOutcome;

    /// <summary>Already holding as many units in this project as the rule allows.</summary>
    public sealed record TooManyHolds(int Limit) : HoldOutcome;

    public sealed record UnitNotFound : HoldOutcome;
}

/// <summary>What a prospect tells us when they reserve.</summary>
public sealed record ProspectDetails(
    string Email,
    string? FullName,
    string? Phone,
    string Locale,
    bool ConsentGiven,
    string ConsentSource);

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
