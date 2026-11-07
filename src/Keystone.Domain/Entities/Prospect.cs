using Keystone.Domain.Common;

namespace Keystone.Domain.Entities;

/// <summary>
/// Someone interested in buying. Not a staff account.
/// </summary>
public class Prospect : Entity
{
    /// <summary>
    /// Unique. SQL Server's default collation is case-insensitive, so <c>Ion@example.ro</c>
    /// and <c>ion@example.ro</c> collide as they should, and cannot become two people.
    /// </summary>
    public required string Email { get; set; }

    public string? FirstName { get; set; }

    public string? Surname { get; set; }

    /// <summary>
    /// First name and surname joined. A persisted computed column, so it can never disagree
    /// with the two parts it is made of — write to those, never to this.
    /// </summary>
    public string? FullName { get; private set; }

    public string? Phone { get; set; }

    /// <summary>Where they live. The operator's buyers are overwhelmingly Romanian.</summary>
    public string? City { get; set; }

    /// <summary>
    /// Null means Romania. The forms only ask for a country when the buyer says they live
    /// somewhere else, so a blank here is an answer rather than a gap.
    /// </summary>
    public string? Country { get; set; }

    public string Locale { get; set; } = "ro";

    /// <summary>Where they came from: "website", "import", "referral".</summary>
    public string? Source { get; set; }

    // --- GDPR -------------------------------------------------------------------
    // Present from the first migration rather than added later. The Equity document
    // explicitly promises the prospect database is used only for its stated purpose,
    // and retrofitting consent onto a live table is genuinely painful.

    public DateTimeOffset? GdprConsentAt { get; set; }

    /// <summary>What they consented to, e.g. "reservations", "marketing".</summary>
    public List<string> GdprConsentPurpose { get; set; } = [];

    /// <summary>Which form and which version of the wording they accepted.</summary>
    public string? GdprConsentSource { get; set; }

    /// <summary>
    /// When this record was stripped of everything identifying, because they asked to be
    /// forgotten and a reservation with money against it had to be kept.
    /// </summary>
    /// <remarks>
    /// This is when it was done, not when they asked — the two were the same field until
    /// <see cref="PrivacyRequest"/> existed to hold the asking, and the old name said the
    /// wrong one of them. Only ever set on records that survived erasure; the rest are gone.
    /// </remarks>
    public DateTimeOffset? AnonymisedAt { get; set; }

    // --- Serial-canceller rule --------------------------------------------------
    // The operator's documents state that a prospect who abandons a hold may not take another,
    // "to avoid reservations cancelled in series by the same person". Today that is
    // enforced by staff memory.

    public int LapsedHoldCount { get; set; }

    public bool CanHold { get; set; } = true;

    public string? BlockReason { get; set; }

    public ICollection<Hold> Holds { get; set; } = [];

    public ICollection<Reservation> Reservations { get; set; } = [];

    public ICollection<WaitlistEntry> WaitlistEntries { get; set; } = [];

    /// <summary>Records a lapse and blocks further holds, per the operator's stated rule.</summary>
    public void RecordLapsedHold()
    {
        LapsedHoldCount++;
        CanHold = false;
        BlockReason = $"Hold lapsed without proceeding ({LapsedHoldCount}).";
    }

    /// <summary>
    /// Blocks by hand, with staff's own reason rather than the automatic one.
    /// </summary>
    /// <remarks>
    /// <see cref="LapsedHoldCount"/> is untouched: it counts holds that lapsed, and a staff
    /// decision is not one of those. Somebody reading "blocked, 0 lapses" can tell at a glance
    /// that a person did this rather than the rule.
    /// </remarks>
    public void Block(string reason)
    {
        CanHold = false;
        BlockReason = reason;
    }

    /// <summary>Lifts a block. Staff action, and audited.</summary>
    public void ClearBlock()
    {
        CanHold = true;
        BlockReason = null;
    }
}

/// <summary>A staff member.</summary>
public class StaffUser : Entity
{
    public required string Email { get; set; }

    public required string Name { get; set; }

    public StaffRole Role { get; set; } = StaffRole.Viewer;

    public bool IsActive { get; set; } = true;
}
