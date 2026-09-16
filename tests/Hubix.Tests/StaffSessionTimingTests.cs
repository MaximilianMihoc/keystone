using Hubix.Web.Auth;
using Shouldly;

namespace Hubix.Tests;

/// <summary>
/// When a staff session is looked at again, and when it simply ends.
/// </summary>
public class StaffSessionTimingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_session_just_checked_is_not_checked_again()
    {
        // The answer changes rarely and the check is a database round trip, so every request
        // asking would be a query per request for nothing.
        StaffSessionValidator.IsDueACheck(Now.AddSeconds(-30), Now).ShouldBeFalse();
    }

    [Fact]
    public void A_stale_check_is_repeated()
    {
        StaffSessionValidator.IsDueACheck(
            Now - StaffSessionValidator.RecheckEvery - TimeSpan.FromSeconds(1), Now).ShouldBeTrue();
    }

    [Fact]
    public void A_session_that_has_never_been_checked_is_checked()
    {
        StaffSessionValidator.IsDueACheck(null, Now).ShouldBeTrue();
    }

    [Fact]
    public void A_fresh_session_is_within_its_ceiling()
    {
        StaffSessionValidator.HasOutlivedItsLimit(Now.AddHours(-1), Now).ShouldBeFalse();
    }

    [Fact]
    public void A_session_kept_alive_past_the_ceiling_ends()
    {
        // The twelve-hour window slides, so without this a session in daily use would renew
        // for ever and a cookie taken once would stay good while somebody kept using it.
        StaffSessionValidator.HasOutlivedItsLimit(
            Now - StaffSessionValidator.AbsoluteLifetime, Now).ShouldBeTrue();
    }

    [Fact]
    public void A_session_with_no_beginning_is_not_trusted()
    {
        // Issued before any of this existed. One sign-in for a team of four beats trusting a
        // cookie nothing has ever checked.
        StaffSessionValidator.HasOutlivedItsLimit(null, Now).ShouldBeTrue();
    }
}
