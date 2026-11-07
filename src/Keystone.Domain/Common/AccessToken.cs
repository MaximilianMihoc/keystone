namespace Keystone.Domain.Common;

/// <summary>
/// Secrets that let someone reach one record of theirs without an account.
/// </summary>
/// <remarks>
/// Deliberately not derived from the primary key. Ids here are UUIDv7, which are time-ordered
/// and so partially predictable — anyone who joined a moment earlier could guess at their
/// neighbours. This is 32 bytes from a cryptographic RNG instead.
/// </remarks>
public static class AccessToken
{
    /// <summary>A URL-safe secret, 256 bits.</summary>
    public static string New() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
