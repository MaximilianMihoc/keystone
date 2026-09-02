using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>
/// A short-lived claim on a unit while a prospect decides.
/// </summary>
/// <remarks>
/// A unit may have many historical holds but only one live one. That is enforced by a filtered
/// unique index on <c>(UnitId) WHERE ReleasedAt IS NULL</c>, not by application discipline.
/// </remarks>
public class Hold : Entity
{
    public Guid UnitId { get; set; }

    public Unit? Unit { get; set; }

    public Guid ProspectId { get; set; }

    public Prospect? Prospect { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    public HoldReleaseReason? ReleaseReason { get; set; }

    public bool IsActive => ReleasedAt is null;

    public bool HasExpired(DateTimeOffset now) => IsActive && ExpiresAt <= now;

    public TimeSpan RemainingAt(DateTimeOffset now) =>
        IsActive && ExpiresAt > now ? ExpiresAt - now : TimeSpan.Zero;

    public void Release(HoldReleaseReason reason, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return; // Idempotent: the expiry job may see the same hold twice.
        }

        ReleasedAt = now;
        ReleaseReason = reason;
        UpdatedAt = now;
    }

    /// <summary>
    /// Whether this release counts against the prospect. Staff releases do not — a member of
    /// staff freeing a unit is not the prospect's fault.
    /// </summary>
    public static bool CountsAsLapse(HoldReleaseReason reason) =>
        reason is HoldReleaseReason.Expired or HoldReleaseReason.Cancelled;
}
