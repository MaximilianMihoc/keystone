using System.Globalization;
using System.Resources;
using Keystone.Web;
using Shouldly;

namespace Keystone.Tests;

public class TrancheLabelTests
{
    private static readonly ResourceManager Resources =
        new("Keystone.Web.Resources.SharedResource", typeof(Display).Assembly);

    [Theory]
    [InlineData("en-GB", "Instalment 1", "Instalment 2")]
    [InlineData("ro-RO", "Tranșa 1", "Tranșa 2")]
    public void Payments_are_numbered_by_position_in_the_readers_language(
        string culture, string first, string second)
    {
        // The apartment page numbers by position rather than printing the stored label, which
        // is the operator's own and untranslatable. Linden's sheet called its second payment "Transa 3".
        var format = Resources.GetString("Tranche.Number", CultureInfo.GetCultureInfo(culture));

        string.Format(CultureInfo.GetCultureInfo(culture), format!, 1).ShouldBe(first);
        string.Format(CultureInfo.GetCultureInfo(culture), format!, 2).ShouldBe(second);
    }

    [Theory]
    [InlineData("en-GB", "Deposit (15%)", "Deposit", "Assignment price")]
    [InlineData("ro-RO", "Avans (15%)", "Avans", "Preț cesiune")]
    public void The_first_instalment_names_the_two_columns_the_sheets_keep_it_in(
        string culture, string advance, string advanceNoRate, string cesiune)
    {
        // Every one of the operator's five sheets splits "Transa 1" into "Avans ... fara TVA" and
        // "Pret cesiune faraTVA". The page shows the same two figures under the instalment.
        var ci = CultureInfo.GetCultureInfo(culture);

        string.Format(ci, Resources.GetString("Tranche.Advance", ci)!, 15).ShouldBe(advance);
        Resources.GetString("Tranche.Cesiune", ci).ShouldBe(cesiune);

        // A payment that is a remainder has no rate to name, so the label drops it
        // rather than the split disappearing and burying the assignment price again.
        Resources.GetString("Tranche.AdvanceNoRate", ci).ShouldBe(advanceNoRate);
    }
}
