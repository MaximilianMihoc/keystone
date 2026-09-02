namespace Hubix.Domain;

/// <summary>
/// The permitted unit transitions, in one place.
/// </summary>
/// <remarks>
/// This is a guard against programmer error, not a concurrency control. Two prospects racing
/// for the same apartment are separated by the conditional UPDATE in the hold service, which
/// relies on a database row lock. Both matter, and neither substitutes for the other.
/// </remarks>
public static class UnitStateMachine
{
    private static readonly Dictionary<UnitState, UnitState[]> Allowed = new()
    {
        [UnitState.Available] = [UnitState.Held, UnitState.Withdrawn],

        // Held returns to Available on expiry, prospect cancellation or staff release,
        // and advances to Reserved once the fee is confirmed.
        [UnitState.Held] = [UnitState.Available, UnitState.Reserved],

        // A reservation can collapse back to Available if it is cancelled.
        [UnitState.Reserved] = [UnitState.Contracted, UnitState.Available],

        // Even a signed cesiune can fall through, though rarely.
        [UnitState.Contracted] = [UnitState.Sold, UnitState.Available],

        // Sold is terminal in normal operation. Reversing it is a correction, and is
        // deliberately not expressible here: it goes through an explicit admin action
        // that writes its own audit trail.
        [UnitState.Sold] = [],

        [UnitState.Withdrawn] = [UnitState.Available]
    };

    public static bool CanTransition(UnitState from, UnitState to) =>
        from != to && Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyCollection<UnitState> TransitionsFrom(UnitState from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];

    /// <exception cref="InvalidUnitTransitionException">The transition is not permitted.</exception>
    public static void EnsureCanTransition(UnitState from, UnitState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidUnitTransitionException(from, to);
        }
    }
}

public sealed class InvalidUnitTransitionException(UnitState from, UnitState to)
    : InvalidOperationException($"A unit cannot move from {from} to {to}.")
{
    public UnitState From { get; } = from;

    public UnitState To { get; } = to;
}
