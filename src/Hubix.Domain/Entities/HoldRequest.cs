using System.Security.Cryptography;
using System.Text;
using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>
/// Somebody has asked to hold a unit and has not yet shown the address is theirs.
/// </summary>
/// <remarks>
/// Nothing here is a hold. The apartment stays on sale, no prospect record is touched, and
/// nobody is blocked, until the link sent to that address is followed — because anybody can
/// type anybody's address into a public form (issue #30). What the form said is kept here
/// until then, so the buyer does not fill it in twice.
/// </remarks>
public class HoldRequest : Entity
{
    public Guid UnitId { get; set; }

    public Unit? Unit { get; set; }

    /// <summary>SHA-256 of the token. The token itself only ever exists in the email.</summary>
    public required string TokenHash { get; set; }

    public required string Email { get; set; }

    public string? FirstName { get; set; }

    public string? Surname { get; set; }

    public string? Phone { get; set; }

    public string? City { get; set; }

    /// <summary>Null means Romania, as on the form.</summary>
    public string? Country { get; set; }

    public string Locale { get; set; } = "ro";

    public bool ConsentGiven { get; set; }

    /// <summary>Which form and which version of the wording they accepted.</summary>
    public string? ConsentSource { get; set; }

    /// <summary>What they asked for from the car park, or null for none.</summary>
    public ParkingSpaceKind? ParkingKind { get; set; }

    public int ParkingQuantity { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public string? RequestedFromIp { get; set; }

    public bool IsUsable(DateTimeOffset now) => ConfirmedAt is null && ExpiresAt > now;

    /// <summary>
    /// How long the link lasts.
    /// </summary>
    /// <remarks>
    /// Longer than a sign-in link, because the person following this one is a buyer who may
    /// be reading email on another device, and shorter than the hold itself: the apartment is
    /// still on sale while they decide, so a stale link is a disappointment, not a loss.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static string NewToken() => Common.AccessToken.New();

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
