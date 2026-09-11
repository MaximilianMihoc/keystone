using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>Which right somebody is exercising.</summary>
public enum PrivacyRequestKind
{
    /// <summary>"Send me everything you hold about me." GDPR Article 15.</summary>
    Access,

    /// <summary>"Delete me." GDPR Article 17.</summary>
    Erasure
}

/// <summary>
/// A record that somebody asked, and when they were answered.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the act of answering, because a regulator asks two different questions: what
/// did you do, and how long did you take. The audit trail answers the first. Only this answers
/// the second, and it cannot be reconstructed afterwards — nothing else in the system knows
/// that an email arrived on the 3rd if it was acted on the 11th.
/// </para>
/// <para>
/// A table of its own rather than columns on <see cref="Prospect"/>, because erasure can delete
/// that row outright. A compliance record that disappears exactly when the erasure it documents
/// happens would be worse than not keeping one.
/// </para>
/// <para>
/// <see cref="Email"/> is cleared when an erasure completes, for the same reason
/// <c>AuditLog.ActorLabel</c> becomes <c>[erased]</c>: the dates are the evidence, and holding
/// the address of somebody who asked to be forgotten in order to prove they were forgotten
/// would defeat the request it records.
/// </para>
/// </remarks>
public class PrivacyRequest : Entity
{
    /// <summary>
    /// Who asked, while the request is open. Null once an erasure has been carried out.
    /// </summary>
    public string? Email { get; set; }

    public PrivacyRequestKind Kind { get; set; }

    /// <summary>
    /// When they asked, which is not when it was logged. Staff enter the date the email or
    /// call actually arrived, because that is the date the clock runs from.
    /// </summary>
    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>What was done: "exported", "deleted", "anonymised".</summary>
    public string? Outcome { get; set; }

    /// <summary>Anything staff need to remember about it. How it arrived, usually.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// How long the law allows. One month from the request, per GDPR Article 12(3).
    /// </summary>
    /// <remarks>
    /// Months rather than 30 days: the Article says "one month", and
    /// <see cref="DateTimeOffset.AddMonths"/> lands on the same date in the next month, which
    /// is how the deadline is actually counted.
    /// </remarks>
    public DateTimeOffset DueAt => RequestedAt.AddMonths(1);

    public bool IsOpen => CompletedAt is null;

    public bool IsOverdue(DateTimeOffset now) => IsOpen && now > DueAt;

    /// <summary>Closes the request, recording what was done about it.</summary>
    public void Complete(string outcome, DateTimeOffset at)
    {
        CompletedAt = at;
        Outcome = outcome;
    }
}
