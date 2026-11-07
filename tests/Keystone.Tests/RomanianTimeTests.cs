using System.Globalization;
using System.Resources;
using Keystone.Web;
using Microsoft.Extensions.Localization;
using Shouldly;

namespace Keystone.Tests;

/// <summary>
/// Deadlines are stated in Romanian time, whatever the server thinks the time is.
/// </summary>
/// <remarks>
/// This is the regression. Every emailed deadline used to go through <c>ToLocalTime()</c>, which
/// converts to the timezone of whatever machine the process runs on. Production is Linux App
/// Service with no timezone configured, so that is UTC, and a buyer in Bucharest was given a
/// deadline two or three hours earlier than the real one. It looked correct in development
/// because a Windows machine in Romania already has Romanian local time, so only the deployed
/// build was ever wrong — which is why this tests the conversion rather than the machine.
/// </remarks>
public class RomanianTimeTests
{
    private static readonly ResourceManager Resources =
        new("Keystone.Web.Resources.SharedResource", typeof(Display).Assembly);

    /// <summary>Reads the real resource files, so the label under test is the shipped one.</summary>
    private sealed class Localizer : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            new(name, Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    [Fact]
    public void Summer_is_three_hours_ahead_of_utc()
    {
        // EEST. Noon UTC on a July afternoon is three o'clock in Bucharest.
        var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        var local = Display.InRomania(summer);

        local.Offset.ShouldBe(TimeSpan.FromHours(3));
        local.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture).ShouldBe("15 Jul 15:00");
    }

    [Fact]
    public void Winter_is_two_hours_ahead_of_utc()
    {
        // EET. The offset is not a constant, so a hard-coded "+3" would be wrong half the year.
        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var local = Display.InRomania(winter);

        local.Offset.ShouldBe(TimeSpan.FromHours(2));
        local.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture).ShouldBe("15 Jan 14:00");
    }

    [Fact]
    public void A_deadline_late_in_the_day_lands_on_the_next_date()
    {
        // Half past eleven UTC in July is half past two the following morning in Bucharest. The
        // staff sign-in email used to say "today", which was wrong here in the hour and the date.
        var late = new DateTimeOffset(2026, 7, 15, 23, 30, 0, TimeSpan.Zero);

        Display.InRomania(late)
            .ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)
            .ShouldBe("16 Jul 02:30");
    }

    [Theory]
    [InlineData("en-GB", "15 Jul 2026 15:00 (Romania time)")]
    [InlineData("ro-RO", "15 iul. 2026 15:00 (ora României)")]
    public void The_stated_moment_names_the_timezone(string culture, string expected)
    {
        // A bare "15:00" in an inbox says nothing about which clock it means, and a hold that
        // lapses is not a thing to be vague about.
        var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

        try
        {
            var ci = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = ci;
            CultureInfo.CurrentUICulture = ci;

            Display.Moment(summer, "dd MMM yyyy HH:mm", new Localizer()).ShouldBe(expected);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }
    }
}
