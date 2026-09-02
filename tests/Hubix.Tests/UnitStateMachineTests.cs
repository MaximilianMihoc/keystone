using Hubix.Domain;
using Hubix.Domain.Entities;
using Shouldly;

namespace Hubix.Tests;

public class UnitStateMachineTests
{
    [Theory]
    [InlineData(UnitState.Available, UnitState.Held)]
    [InlineData(UnitState.Available, UnitState.Withdrawn)]
    [InlineData(UnitState.Held, UnitState.Available)]      // expiry, cancellation, staff release
    [InlineData(UnitState.Held, UnitState.Reserved)]       // fee paid
    [InlineData(UnitState.Reserved, UnitState.Contracted)] // cesiune at the notary
    [InlineData(UnitState.Reserved, UnitState.Available)]  // cancelled
    [InlineData(UnitState.Contracted, UnitState.Sold)]
    [InlineData(UnitState.Withdrawn, UnitState.Available)]
    public void Permits_the_transitions_the_business_actually_performs(UnitState from, UnitState to)
    {
        UnitStateMachine.CanTransition(from, to).ShouldBeTrue();
    }

    [Theory]
    [InlineData(UnitState.Available, UnitState.Reserved)]   // must pass through Held
    [InlineData(UnitState.Available, UnitState.Sold)]
    [InlineData(UnitState.Held, UnitState.Contracted)]      // no skipping the fee
    [InlineData(UnitState.Held, UnitState.Sold)]
    [InlineData(UnitState.Reserved, UnitState.Sold)]        // the notary step is not optional
    [InlineData(UnitState.Sold, UnitState.Available)]       // corrections go through an explicit action
    [InlineData(UnitState.Sold, UnitState.Held)]
    public void Refuses_transitions_that_would_skip_a_step(UnitState from, UnitState to)
    {
        UnitStateMachine.CanTransition(from, to).ShouldBeFalse();
    }

    [Theory]
    [InlineData(UnitState.Available)]
    [InlineData(UnitState.Held)]
    [InlineData(UnitState.Sold)]
    public void A_state_never_transitions_to_itself(UnitState state)
    {
        UnitStateMachine.CanTransition(state, state).ShouldBeFalse();
    }

    [Fact]
    public void Sold_is_terminal()
    {
        UnitStateMachine.TransitionsFrom(UnitState.Sold).ShouldBeEmpty();
    }

    [Fact]
    public void EnsureCanTransition_names_both_states_when_it_throws()
    {
        var ex = Should.Throw<InvalidUnitTransitionException>(
            () => UnitStateMachine.EnsureCanTransition(UnitState.Available, UnitState.Sold));

        ex.From.ShouldBe(UnitState.Available);
        ex.To.ShouldBe(UnitState.Sold);
    }
}

public class HoldTests
{
    private static Hold ActiveHold(DateTimeOffset expiresAt) => new()
    {
        UnitId = Guid.CreateVersion7(),
        ProspectId = Guid.CreateVersion7(),
        AccessToken = Hold.NewAccessToken(),
        ExpiresAt = expiresAt
    };

    [Fact]
    public void Releasing_twice_is_idempotent()
    {
        // The expiry job can see the same hold twice; the second pass must not
        // overwrite the reason recorded by the first.
        var now = DateTimeOffset.UtcNow;
        var hold = ActiveHold(now.AddHours(-1));

        hold.Release(HoldReleaseReason.Expired, now);
        hold.Release(HoldReleaseReason.Admin, now.AddMinutes(5));

        hold.ReleaseReason.ShouldBe(HoldReleaseReason.Expired);
        hold.ReleasedAt.ShouldBe(now);
    }

    [Fact]
    public void A_released_hold_has_not_expired()
    {
        var now = DateTimeOffset.UtcNow;
        var hold = ActiveHold(now.AddHours(-1));
        hold.Release(HoldReleaseReason.Converted, now);

        hold.HasExpired(now).ShouldBeFalse();
        hold.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Remaining_time_never_goes_negative()
    {
        var now = DateTimeOffset.UtcNow;

        ActiveHold(now.AddHours(-3)).RemainingAt(now).ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(HoldReleaseReason.Expired, true)]
    [InlineData(HoldReleaseReason.Cancelled, true)]
    [InlineData(HoldReleaseReason.Admin, false)]     // staff error is not the prospect's fault
    [InlineData(HoldReleaseReason.Converted, false)] // they bought it
    public void Only_a_prospects_own_lapse_counts_against_them(HoldReleaseReason reason, bool counts)
    {
        Hold.CountsAsLapse(reason).ShouldBe(counts);
    }

    [Fact]
    public void A_lapse_blocks_further_holds_per_Hubix_rule()
    {
        var prospect = new Prospect { Email = "test@example.ro" };

        prospect.CanHold.ShouldBeTrue();
        prospect.RecordLapsedHold();

        prospect.CanHold.ShouldBeFalse();
        prospect.LapsedHoldCount.ShouldBe(1);
        prospect.BlockReason.ShouldNotBeNull();

        prospect.ClearBlock();
        prospect.CanHold.ShouldBeTrue();
        prospect.LapsedHoldCount.ShouldBe(1); // history is kept
    }
}
