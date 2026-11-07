using Keystone.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keystone.Infrastructure.Auth;

public sealed record LoginLink(string Token, string Email, string Name, DateTimeOffset ExpiresAt);

/// <summary>
/// Passwordless sign-in for Hubix staff.
/// </summary>
public sealed class StaffAuthService(
    KeystoneDbContext db,
    TimeProvider clock,
    ILogger<StaffAuthService> logger)
{
    /// <summary>
    /// Whether an open session's account may still be used, as its cookie describes it.
    /// </summary>
    /// <remarks>
    /// Role and identity are written into the cookie at sign-in, so something has to keep
    /// asking: without this, deactivating or demoting somebody changed a column and left their
    /// live session fully privileged until the cookie lapsed (issue #34).
    /// <para>
    /// A role that no longer matches fails, rather than being quietly corrected. The claims
    /// were handed out at sign-in, and re-issuing them mid-request would leave whatever the
    /// request had already decided standing on the old ones.
    /// </para>
    /// </remarks>
    public async Task<bool> IsSessionStillGoodAsync(
        Guid staffId,
        string? roleClaim,
        CancellationToken ct)
    {
        var staff = await db.StaffUsers
            .AsNoTracking()
            .Where(u => u.Id == staffId)
            .Select(u => new { u.IsActive, u.Role })
            .FirstOrDefaultAsync(ct);

        return staff is not null
               && staff.IsActive
               && string.Equals(roleClaim, staff.Role.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Issues a sign-in link, or returns null when the address is not a known active account.
    /// </summary>
    /// <remarks>
    /// The caller must show the same confirmation either way. Telling an unauthenticated
    /// visitor whether an address exists hands them a list of who works at Hubix.
    /// </remarks>
    public async Task<LoginLink?> IssueLoginTokenAsync(string email, string? ip, CancellationToken ct)
    {
        var normalised = email.Trim();

        var staff = await db.StaffUsers
            .FirstOrDefaultAsync(u => u.Email == normalised && u.IsActive, ct);

        if (staff is null)
        {
            logger.LogInformation("Sign-in requested for unknown or inactive address");
            return null;
        }

        var now = clock.GetUtcNow();

        // Any earlier link is void the moment a new one is asked for, so a forwarded or
        // intercepted older email cannot still be used.
        await db.StaffLoginTokens
            .Where(t => t.StaffUserId == staff.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);

        var token = StaffLoginToken.NewToken();

        db.StaffLoginTokens.Add(new StaffLoginToken
        {
            StaffUserId = staff.Id,
            TokenHash = StaffLoginToken.Hash(token),
            ExpiresAt = now.Add(StaffLoginToken.Lifetime),
            RequestedFromIp = ip
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Sign-in link issued for {Email}", staff.Email);

        return new LoginLink(token, staff.Email, staff.Name, now.Add(StaffLoginToken.Lifetime));
    }

    /// <summary>
    /// Consumes a sign-in token and returns the account it belongs to.
    /// </summary>
    /// <remarks>
    /// Single use. Consuming and validating happen in one conditional update, so two requests
    /// arriving with the same token cannot both succeed — the same guarded-write shape the
    /// hold service uses.
    /// </remarks>
    public async Task<StaffUser?> ConsumeAsync(string token, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = StaffLoginToken.Hash(token);

        var consumed = await db.StaffLoginTokens
            .Where(t => t.TokenHash == hash && t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ConsumedAt, now)
                .SetProperty(t => t.UpdatedAt, now), ct);

        if (consumed == 0)
        {
            logger.LogWarning("Sign-in attempted with an expired, unknown or already used token");
            return null;
        }

        var staff = await db.StaffLoginTokens
            .Where(t => t.TokenHash == hash)
            .Select(t => t.StaffUser)
            .FirstOrDefaultAsync(ct);

        if (staff is null || !staff.IsActive)
        {
            // Deactivated between requesting the link and following it.
            return null;
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = staff.Id,
            ActorLabel = staff.Email,
            EntityType = "staff",
            EntityId = staff.Id,
            Action = "staff.signed_in"
        });

        await db.SaveChangesAsync(ct);

        return staff;
    }

    /// <summary>Removes spent and expired tokens. Housekeeping, run from the expiry job.</summary>
    public Task<int> PurgeStaleTokensAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddDays(-7);

        return db.StaffLoginTokens
            .Where(t => t.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }
}
