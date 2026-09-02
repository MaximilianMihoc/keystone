using Hubix.Domain;
using Hubix.Domain.Entities;
using Shouldly;

namespace Hubix.Tests;

/// <summary>
/// The pipeline table is the one place the sales process is written down, so these tests are
/// mostly about the table staying honest with itself and with the unit state machine.
/// </summary>
public class ReservationPipelineTests
{
    private static Reservation At(ReservationState state) =>
        new() { UnitId = Guid.CreateVersion7(), ProspectId = Guid.CreateVersion7(), State = state };

    [Fact]
    public void Every_open_state_is_a_stage_and_nothing_else_is()
    {
        var staged = ReservationPipeline.Stages.Select(s => s.State).ToList();

        var expected = Enum.GetValues<ReservationState>()
            .Where(s => s != ReservationState.Cancelled)
            .ToList();

        // A state added to the enum without a stage would be unreachable on the board and
        // would have no unit state behind it. Catching that here is the point.
        staged.ShouldBe(expected);
    }

    [Fact]
    public void Each_stage_names_the_same_date_field_its_accessor_reads()
    {
        // DateField exists so the board can show a stage's date without a second copy of the
        // pipeline written in TypeScript. It duplicates what GetDate reads, so this holds the
        // two together; without it they could drift in silence.
        foreach (var stage in ReservationPipeline.Stages.Where(s => s.DateField is not null))
        {
            var property = typeof(Reservation).GetProperty(stage.DateField!);
            property.ShouldNotBeNull($"{stage.State} names a field Reservation does not have.");

            var reservation = At(stage.State);
            var stamped = DateTimeOffset.UtcNow;
            property!.SetValue(reservation, stamped);

            stage.GetDate.ShouldNotBeNull();
            stage.GetDate!(reservation).ShouldBe(stamped);
        }
    }

    [Fact]
    public void Every_stage_that_shows_a_date_names_it()
    {
        foreach (var stage in ReservationPipeline.Stages.Where(s => s.GetDate is not null))
        {
            stage.DateField.ShouldNotBeNull($"{stage.State} has a date the board cannot find.");
        }
    }

    [Fact]
    public void The_unit_states_a_pipeline_walks_are_ones_the_state_machine_allows()
    {
        var unit = UnitState.Reserved;

        foreach (var stage in ReservationPipeline.Stages)
        {
            if (stage.Unit == unit)
            {
                continue;
            }

            UnitStateMachine.CanTransition(unit, stage.Unit).ShouldBeTrue(
                $"A unit cannot go from {unit} to {stage.Unit}, which stage {stage.State} needs.");

            unit = stage.Unit;
        }
    }

    [Fact]
    public void A_step_back_down_the_pipeline_can_take_the_unit_with_it()
    {
        // Stepping back is how a mis-click is corrected, and the unit has to be able to
        // follow. Completed is excluded: its unit is Sold, and the pipeline refuses to
        // move it at all.
        var open = ReservationPipeline.Stages
            .Where(s => ReservationPipeline.IsOpen(s.State))
            .ToList();

        for (var i = 1; i < open.Count; i++)
        {
            var from = open[i];
            var to = open[i - 1];

            if (from.Unit != to.Unit)
            {
                UnitStateMachine.CanTransition(from.Unit, to.Unit).ShouldBeTrue(
                    $"Stepping back from {from.State} to {to.State} would strand the unit.");
            }
        }
    }

    [Fact]
    public void Cancelling_returns_the_unit_to_sale_from_every_open_stage()
    {
        foreach (var stage in ReservationPipeline.Stages.Where(s => ReservationPipeline.IsOpen(s.State)))
        {
            UnitStateMachine.CanTransition(stage.Unit, ReservationPipeline.CancelledUnitState)
                .ShouldBeTrue($"A cancelled reservation at {stage.State} would strand its unit.");
        }
    }

    [Fact]
    public void A_step_forward_is_allowed()
    {
        ReservationPipeline.Refuse(At(ReservationState.PendingFee), ReservationState.FeePaid)
            .ShouldBeNull();
    }

    [Fact]
    public void A_jump_forward_is_allowed_because_sales_arrive_half_finished()
    {
        // Staff entering a sale that was already at the notary when this system arrived should
        // not have to click through four stages it never saw.
        ReservationPipeline.Refuse(At(ReservationState.PendingFee), ReservationState.CesiuneSigned)
            .ShouldBeNull();
    }

    [Fact]
    public void A_jump_forward_still_walks_the_unit_through_every_state_in_between()
    {
        var path = ReservationPipeline.UnitPath(
            UnitState.Reserved, ReservationState.PendingFee, ReservationState.Completed);

        // Reserved straight to Sold is not a transition the state machine allows, and skipping
        // a stage must not turn it into one.
        path.ShouldBe([UnitState.Contracted, UnitState.Sold]);
    }

    [Fact]
    public void Moving_between_stages_that_share_a_unit_state_moves_no_unit()
    {
        ReservationPipeline.UnitPath(
                UnitState.Reserved, ReservationState.FeePaid, ReservationState.AgreementSigned)
            .ShouldBeEmpty();
    }

    [Fact]
    public void One_step_back_is_a_correction_and_is_allowed()
    {
        ReservationPipeline.Refuse(At(ReservationState.AgreementSent), ReservationState.FeePaid)
            .ShouldBeNull();
    }

    [Fact]
    public void Two_steps_back_at_once_are_refused()
    {
        ReservationPipeline.Refuse(At(ReservationState.AgreementSigned), ReservationState.FeePaid)
            .ShouldBe("Step back one stage at a time.");
    }

    [Fact]
    public void Booking_the_notary_needs_an_appointment_date()
    {
        var reservation = At(ReservationState.AgreementSigned);

        ReservationPipeline.Refuse(reservation, ReservationState.NotaryScheduled)
            .ShouldNotBeNull();

        reservation.NotaryAppointmentAt = DateTimeOffset.UtcNow.AddDays(7);

        ReservationPipeline.Refuse(reservation, ReservationState.NotaryScheduled)
            .ShouldBeNull();
    }

    [Fact]
    public void A_completed_sale_cannot_be_moved()
    {
        // The unit is Sold, and Sold is terminal. Undoing it is a correction with
        // consequences, not a click on a board.
        ReservationPipeline.Refuse(At(ReservationState.Completed), ReservationState.CesiuneSigned)
            .ShouldNotBeNull();

        ReservationPipeline.Refuse(At(ReservationState.Completed), ReservationState.Cancelled)
            .ShouldNotBeNull();
    }

    [Fact]
    public void A_cancelled_reservation_stays_cancelled()
    {
        ReservationPipeline.Refuse(At(ReservationState.Cancelled), ReservationState.PendingFee)
            .ShouldNotBeNull();
    }

    [Fact]
    public void Cancelling_is_allowed_from_anywhere_open()
    {
        foreach (var stage in ReservationPipeline.Stages.Where(s => ReservationPipeline.IsOpen(s.State)))
        {
            ReservationPipeline.Refuse(At(stage.State), ReservationState.Cancelled).ShouldBeNull();
        }
    }

    [Fact]
    public void Stepping_back_clears_the_date_the_stage_stamped()
    {
        var reservation = At(ReservationState.FeePaid);
        var stage = ReservationPipeline.For(ReservationState.FeePaid);

        stage.SetDate!(reservation, DateTimeOffset.UtcNow);
        stage.GetDate!(reservation).ShouldNotBeNull();

        stage.SetDate(reservation, null);
        stage.GetDate(reservation).ShouldBeNull();
    }
}
