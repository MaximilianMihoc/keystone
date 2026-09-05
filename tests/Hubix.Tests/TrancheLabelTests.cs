using System.Globalization;
using System.Resources;
using Hubix.Web;
using Shouldly;

namespace Hubix.Tests;

public class TrancheLabelTests
{
    private static readonly ResourceManager Resources =
        new("Hubix.Web.Resources.SharedResource", typeof(Display).Assembly);

    [Theory]
    [InlineData("en-GB", "Instalment 1", "Instalment 2")]
    [InlineData("ro-RO", "Tranșa 1", "Tranșa 2")]
    public void Payments_are_numbered_by_position_in_the_readers_language(
        string culture, string first, string second)
    {
        // The apartment page numbers by position rather than printing the stored label, which
        // is Hubix's own and untranslatable. Roka's sheet called its second payment "Transa 3".
        var format = Resources.GetString("Tranche.Number", CultureInfo.GetCultureInfo(culture));

        string.Format(CultureInfo.GetCultureInfo(culture), format!, 1).ShouldBe(first);
        string.Format(CultureInfo.GetCultureInfo(culture), format!, 2).ShouldBe(second);
    }
}
