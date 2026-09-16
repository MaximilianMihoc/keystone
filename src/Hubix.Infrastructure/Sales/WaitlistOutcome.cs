namespace Hubix.Infrastructure.Sales;

/// <summary>What happened when someone tried to join a queue.</summary>
public abstract record WaitlistOutcome
{
    // Neither carries the entry's access token. These answer a public form, and anybody can
    // type somebody else's address into one; the token is the whole credential for an entry,
    // so it goes only into the email sent to the address on file (issue #29).

    /// <summary>They are on the list.</summary>
    public sealed record Joined(Guid EntryId, int Place) : WaitlistOutcome;

    /// <summary>
    /// They were already on it, and this is where. Not an error: somebody who submits the
    /// form twice gets their link sent again, not told off.
    /// </summary>
    public sealed record AlreadyWaiting(Guid EntryId, int Place) : WaitlistOutcome;

    public sealed record ProjectNotFound : WaitlistOutcome;

    /// <summary>The apartment type does not belong to this project.</summary>
    public sealed record UnknownType : WaitlistOutcome;
}

/// <summary>Somebody's place in a queue, as shown to them.</summary>
/// <param name="Place">Counted at the time of asking, not stored.</param>
/// <param name="TypeName">Null when they will take any apartment.</param>
public sealed record WaitlistPlace(
    Guid EntryId,
    string ProjectName,
    string ProjectSlug,
    string? TypeName,
    int Place,
    int QueueLength,
    DateTimeOffset JoinedAt,
    DateTimeOffset? NotifiedAt,
    DateTimeOffset? RemovedAt)
{
    public bool IsWaiting => RemovedAt is null && NotifiedAt is null;
}
