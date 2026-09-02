using System.Security.Cryptography;
using System.Text;
using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>
/// A single-use sign-in link for a member of Hubix staff.
/// </summary>
/// <remarks>
/// <para>
/// Passwordless, for a team of four. No password store means no hashing policy to get wrong,
/// no reset flow to build, and nothing worth stealing if the database leaks.
/// </para>
/// <para>
/// Only the hash is stored. A token in the database is a live credential; treating it like a
/// password is the whole point.
/// </para>
/// </remarks>
public class StaffLoginToken : Entity
{
    public Guid StaffUserId { get; set; }

    public StaffUser? StaffUser { get; set; }

    /// <summary>SHA-256 of the token. The token itself only ever exists in the email.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public string? RequestedFromIp { get; set; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now;

    /// <summary>How long a link stays valid. Long enough to find the email, short enough to matter.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
