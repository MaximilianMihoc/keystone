using System.Globalization;
using System.Resources;
using Hubix.Domain;
using Hubix.Web;
using Shouldly;

namespace Hubix.Tests;

public class ParkingLabelTests
{
    private static readonly ResourceManager Resources =
        new("Hubix.Web.Resources.SharedResource", typeof(Display).Assembly);

    [Theory]
    [InlineData("en-GB", "Parking", "Deposit + assignment", "Space")]
    [InlineData("ro-RO", "Parcare", "Avans + cesiune", "Tip loc")]
    public void The_parking_section_is_labelled_in_the_readers_language(
        string culture, string heading, string deposit, string space)
    {
        var ci = CultureInfo.GetCultureInfo(culture);

        Resources.GetString("Parking.Heading", ci).ShouldBe(heading);
        Resources.GetString("Parking.Advance", ci).ShouldBe(deposit);
        Resources.GetString("Parking.Space", ci).ShouldBe(space);
    }

    [Theory]
    [InlineData("en-GB")]
    [InlineData("ro-RO")]
    public void Every_kind_of_space_has_a_word_in_every_language(string culture)
    {
        // The point of the enum is that the description translates. A new member with no
        // resource entry would otherwise reach a visitor as "AboveGroundCovered".
        var ci = CultureInfo.GetCultureInfo(culture);

        foreach (var kind in Enum.GetValues<ParkingSpaceKind>())
        {
            Resources.GetString(Display.ParkingSpaceKey(kind), ci)
                .ShouldNotBeNullOrWhiteSpace($"{kind} has no {culture} label");
        }
    }
}
