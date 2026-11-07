using Keystone.Domain.Entities;

namespace Keystone.Domain;

/// <summary>
/// One stage of the sales pipeline.
/// </summary>
/// <param name="State">The persisted value. This is the vocabulary; the rest is behaviour.</param>
/// <param name="Label">What the board shows, in English.</param>
/// <param name="SourceColumn">
/// The operator's own spreadsheet column, where this stage came from one. Kept so the mapping back to
/// what they use today is explicit rather than folklore, without putting Romanian column
/// headings into an otherwise English interface.
/// </param>
/// <param name="Unit">The unit state implied by a reservation sitting at this stage.</param>
/// <param name="GetDate">Reads the date this stage is about, where it has one.</param>
/// <param name="SetDate">
/// Stamps that date on arrival and clears it on a step back. Null means the stage stamps
/// nothing automatically.
/// </param>
/// <param name="Requires">
/// Returns why the reservation is not ready for this stage, or null when it is.
/// </param>
/// <param name="DateField">
/// The name of the property <paramref name="GetDate"/> reads. Redundant in C#, and there so the
/// board can show each stage's date without a second copy of the pipeline written in TypeScript.
/// A test holds the two in agreement.
/// </param>
public sealed record PipelineStage(
    ReservationState State,
    string Label,
    string? SourceColumn,
    UnitState Unit,
    Func<Reservation, DateTimeOffset?>? GetDate = null,
    Action<Reservation, DateTimeOffset?>? SetDate = null,
    Func<Reservation, string?>? Requires = null,
    string? DateField = null)
{
    public bool RecordsDate => SetDate is not null;
}

/// <summary>
/// The sales pipeline: its order, what each stage means for the unit, and what may move where.
/// </summary>
/// <remarks>
/// <para>
/// Every rule about the pipeline lives in the <see cref="Stages"/> table and the two methods
/// under it. Nothing else in the system switches on <see cref="ReservationState"/>, and that is
/// the entire point of this type. The pipeline was inferred from the operator's documents and the
/// hidden columns of their spreadsheets, not observed, so at least one thing about it is wrong.
/// When they tell us what it really is, the correction is an edit to this list — a stage
/// renamed, reordered, inserted or dropped — rather than a hunt through services, endpoints
/// and a board.
/// </para>
/// <para>
/// Deliberately not built: per-project pipelines held in the database. As far as the documents
/// show, five projects share one process, and a configuration table nobody has asked for is a
/// schema to maintain and a screen to build for a variation that may not exist.
/// </para>
/// </remarks>
public static class ReservationPipeline
{
    /// <summary>
    /// The open pipeline, in order. <see cref="ReservationState.Cancelled"/> is not in it: it is
    /// reachable from anywhere rather than being a step, so it is handled separately.
    /// </summary>
    public static IReadOnlyList<PipelineStage> Stages { get; } =
    [
        new(ReservationState.PendingFee,
            "Awaiting fee",
            null,
            UnitState.Reserved),

        new(ReservationState.FeePaid,
            "Fee paid",
            "Platit rezervare",
            UnitState.Reserved,
            r => r.FeePaidAt,
            (r, d) => r.FeePaidAt = d,
            DateField: nameof(Reservation.FeePaidAt)),

        new(ReservationState.AgreementSent,
            "Agreement sent",
            null,
            UnitState.Reserved,
            r => r.AgreementSentAt,
            (r, d) => r.AgreementSentAt = d,
            DateField: nameof(Reservation.AgreementSentAt)),

        new(ReservationState.AgreementSigned,
            "Agreement signed",
            "Trimis rezervarea semnata",
            UnitState.Reserved,
            r => r.SignedAgreementAt,
            (r, d) => r.SignedAgreementAt = d,
            DateField: nameof(Reservation.SignedAgreementAt)),

        // Stamps nothing. Reaching this stage and the appointment itself are different
        // moments, and the appointment is the one anybody needs: it is in the future, staff
        // type it in, and it answers "what is happening on Thursday".
        new(ReservationState.NotaryScheduled,
            "Notary booked",
            null,
            UnitState.Reserved,
            r => r.NotaryAppointmentAt,
            null,
            r => r.NotaryAppointmentAt is null
                ? "Set the notary appointment date before recording the booking."
                : null,
            DateField: nameof(Reservation.NotaryAppointmentAt)),

        new(ReservationState.CesiuneSigned,
            "Cesiune signed",
            "Cesiune semnata la NOTAR",
            UnitState.Contracted,
            r => r.CesiuneSignedAt,
            (r, d) => r.CesiuneSignedAt = d,
            DateField: nameof(Reservation.CesiuneSignedAt)),

        new(ReservationState.Completed,
            "Completed",
            null,
            UnitState.Sold,
            r => r.CompletedAt,
            (r, d) => r.CompletedAt = d,
            DateField: nameof(Reservation.CompletedAt))
    ];

    /// <summary>Where a cancelled reservation leaves its unit.</summary>
    public const UnitState CancelledUnitState = UnitState.Available;

    public static PipelineStage For(ReservationState state) =>
        Stages.FirstOrDefault(s => s.State == state)
        ?? throw new ArgumentOutOfRangeException(
            nameof(state), state, "That state is not a stage of the open pipeline.");

    /// <summary>Position in the pipeline, or -1 for a state that is not a stage.</summary>
    public static int IndexOf(ReservationState state)
    {
        for (var i = 0; i < Stages.Count; i++)
        {
            if (Stages[i].State == state)
            {
                return i;
            }
        }

        return -1;
    }

    public static bool IsOpen(ReservationState state) =>
        state is not (ReservationState.Completed or ReservationState.Cancelled);

    /// <summary>
    /// Why <paramref name="reservation"/> may not move to <paramref name="target"/>, or null
    /// when it may.
    /// </summary>
    /// <remarks>
    /// Forward is any distance: staff entering a sale that was already halfway through when
    /// this system arrived should not have to click through five stages it never saw. Skipped
    /// stages keep their empty dates, which is truthful — nobody knows when they happened.
    /// Backward is one stage at a time, because a step back is a correction and each one is
    /// worth its own audit row.
    /// </remarks>
    public static string? Refuse(Reservation reservation, ReservationState target)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        var current = reservation.State;

        if (current == ReservationState.Cancelled)
        {
            return "This reservation was cancelled. Start a new one on the unit instead.";
        }

        if (current == ReservationState.Completed)
        {
            // The unit is Sold, and Sold has no way out of the state machine. Undoing a
            // completed sale is a correction with consequences, not a click on a board.
            return "This sale is complete and cannot be moved.";
        }

        if (target == ReservationState.Cancelled)
        {
            return null;
        }

        var from = IndexOf(current);
        var to = IndexOf(target);

        if (to < 0)
        {
            return $"{target} is not a stage of the pipeline.";
        }

        if (to < from - 1)
        {
            return "Step back one stage at a time.";
        }

        return to > from ? For(target).Requires?.Invoke(reservation) : null;
    }

    /// <summary>
    /// The unit states this move walks through, in order.
    /// </summary>
    /// <remarks>
    /// A forward jump crosses every stage in between, so it collects their unit states rather
    /// than leaping to the last one. Reserved to Sold is not a transition the state machine
    /// allows, and it should not become one just because somebody skipped a stage.
    /// </remarks>
    public static IReadOnlyList<UnitState> UnitPath(
        UnitState unitState,
        ReservationState from,
        ReservationState to)
    {
        var wanted = to == ReservationState.Cancelled
            ? [CancelledUnitState]
            : Crossed(from, to).Select(s => s.Unit);

        var path = new List<UnitState>();
        var at = unitState;

        foreach (var state in wanted)
        {
            if (state != at)
            {
                path.Add(state);
                at = state;
            }
        }

        return path;
    }

    /// <summary>The stages a move passes through, the target included.</summary>
    private static IEnumerable<PipelineStage> Crossed(ReservationState from, ReservationState to)
    {
        var start = IndexOf(from);
        var end = IndexOf(to);

        return end > start
            ? Stages.Skip(start + 1).Take(end - start)
            : [Stages[end]];
    }
}
