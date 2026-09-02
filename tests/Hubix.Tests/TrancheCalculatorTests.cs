using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Shouldly;

namespace Hubix.Tests;

public class TrancheCalculatorTests
{
    /// <summary>
    /// Roka Park View: 15% advance on the developer price, 19% VAT, Transa 1 then Transa 3.
    /// </summary>
    /// <remarks>
    /// The prospectus advertises "~14%", which is 14% of the gross total. The spreadsheet's
    /// actual arithmetic is exactly 15% of (total - cesiune). The tests below pin that.
    /// </remarks>
    private static PricingRule RokaRule() => new()
    {
        AdvancePct = 0.1500m,
        CesiuneVatPct = 0.1900m,
        VatStandard = 0.1900m,
        VatReduced = 0.0900m,
        Tranches =
        [
            new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new TrancheDefinition("Transa 3", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]
    };

    /// <summary>Torontal: 20% advance, otherwise the same shape.</summary>
    private static PricingRule TorontalRule() => new()
    {
        AdvancePct = 0.2000m,
        CesiuneVatPct = 0.1900m,
        VatStandard = 0.1900m,
        VatReduced = 0.0900m,
        Tranches =
        [
            new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new TrancheDefinition("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]
    };

    // ---------------------------------------------------------------------------------
    // Golden cases lifted straight from Hubix's live inventory sheets. If these break,
    // the calculator has stopped agreeing with the business, whatever else still passes.
    // ---------------------------------------------------------------------------------

    [Theory]
    // code,     total,       cesiune,    expected advance, expected remainder
    [InlineData("Ap.102", 134_859, 14_346.93, 18_076.81, 102_435.26)]
    [InlineData("Ap.103", 134_454, 14_303.87, 18_022.52, 102_127.61)]
    [InlineData("Ap.104", 135_776, 14_444.62, 18_199.71, 103_131.67)]
    [InlineData("Ap.105", 142_841, 15_196.07, 19_146.74, 108_498.19)]
    public void Reproduces_the_Roka_spreadsheet(
        string code, decimal total, decimal cesiune, decimal advance, decimal remainder)
    {
        var result = TrancheCalculator.Calculate(RokaRule(), total, cesiune, reducedVatApplies: false);

        result.Tranches[0].ApartmentNet.ShouldBe(advance, 0.01m, code);
        result.Tranches[1].ApartmentNet.ShouldBe(remainder, 0.01m, code);
        result.Tranches[0].CesiuneNet.ShouldBe(cesiune, code);
    }

    [Theory]
    [InlineData(103_628.49, 9_508.56, 18_823.99, 75_295.94)]
    [InlineData(112_844.65, 10_338.02, 20_501.33, 82_005.30)]
    [InlineData(85_475.29, 7_874.78, 15_520.10, 62_080.41)]
    public void Reproduces_the_Torontal_spreadsheet(
        decimal total, decimal cesiune, decimal advance, decimal remainder)
    {
        var result = TrancheCalculator.Calculate(TorontalRule(), total, cesiune, reducedVatApplies: false);

        result.Tranches[0].ApartmentNet.ShouldBe(advance, 0.01m);
        result.Tranches[1].ApartmentNet.ShouldBe(remainder, 0.01m);
    }

    [Fact]
    public void The_cesiune_is_part_of_the_total_not_added_to_it()
    {
        // advance + cesiune + remainder == the price on the sheet. This is the invariant
        // that was got wrong first time round: the assignment fee is Hubix's margin taken
        // out of what the buyer pays, not a surcharge.
        var result = TrancheCalculator.Calculate(RokaRule(), 134_859m, 14_346.93m, false);

        result.TotalNet.ShouldBe(134_859m);
        result.DeveloperPriceNet.ShouldBe(120_512.07m);
    }

    [Fact]
    public void Apartment_portions_sum_to_the_developer_price()
    {
        var result = TrancheCalculator.Calculate(RokaRule(), 134_859m, 14_346.93m, false);

        result.Tranches.Sum(t => t.ApartmentNet).ShouldBe(result.DeveloperPriceNet);
    }

    [Theory]
    [InlineData(134_859, 14_346.93)]
    [InlineData(110_755, 11_783.32)]
    [InlineData(142_841, 15_196.07)]
    [InlineData(99_999.99, 0.01)]
    [InlineData(0.02, 0.01)]
    public void The_final_tranche_absorbs_rounding_so_nothing_is_lost(decimal price, decimal cesiune)
    {
        var result = TrancheCalculator.Calculate(RokaRule(), price, cesiune, false);

        // A penny lost here is a penny a buyer is wrongly invoiced for.
        result.TotalNet.ShouldBe(price);
    }

    [Fact]
    public void Reduced_vat_applies_only_when_the_buyer_qualifies()
    {
        var standard = TrancheCalculator.Calculate(RokaRule(), 100_000m, 0m, reducedVatApplies: false);
        var reduced = TrancheCalculator.Calculate(RokaRule(), 100_000m, 0m, reducedVatApplies: true);

        standard.VatRate.ShouldBe(0.1900m);
        reduced.VatRate.ShouldBe(0.0900m);
        reduced.TotalVat.ShouldBeLessThan(standard.TotalVat);
    }

    [Fact]
    public void Falls_back_to_the_standard_rate_when_the_project_has_no_reduced_rate()
    {
        var rule = RokaRule();
        rule.VatReduced = null;

        var result = TrancheCalculator.Calculate(rule, 100_000m, 0m, reducedVatApplies: true);

        result.VatRate.ShouldBe(0.1900m);
    }

    [Fact]
    public void Vat_on_the_cesiune_uses_its_own_rate()
    {
        // Cartierul Vienez charges 21% on the assignment while the apartment can sit at 9%.
        var rule = RokaRule();
        rule.CesiuneVatPct = 0.2100m;

        var result = TrancheCalculator.Calculate(rule, 110_000m, 10_000m, reducedVatApplies: true);

        result.DeveloperPriceNet.ShouldBe(100_000m);
        result.Tranches[0].CesiuneVat.ShouldBe(2_100m);   // 10,000 cesiune at 21%
        result.Tranches[0].ApartmentVat.ShouldBe(1_350m); // 15,000 advance at 9%
    }

    [Fact]
    public void A_three_tranche_schedule_allocates_the_interim_payment()
    {
        var rule = RokaRule();
        rule.Tranches =
        [
            new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new TrancheDefinition("Transa 2", TrancheBasis.Percentage, false, TrancheDue.Interim, 0.30m),
            new TrancheDefinition("Transa 3", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ];

        var result = TrancheCalculator.Calculate(rule, 110_000m, 10_000m, false);

        result.DeveloperPriceNet.ShouldBe(100_000m);
        result.Tranches[0].ApartmentNet.ShouldBe(15_000m);
        result.Tranches[1].ApartmentNet.ShouldBe(30_000m);
        result.Tranches[2].ApartmentNet.ShouldBe(55_000m);
    }

    [Fact]
    public void Refuses_a_cesiune_larger_than_the_total()
    {
        Should.Throw<InvalidOperationException>(
            () => TrancheCalculator.Calculate(RokaRule(), 10_000m, 20_000m, false));
    }

    [Fact]
    public void Refuses_a_cesiune_that_no_tranche_collects()
    {
        var rule = RokaRule();
        rule.Tranches =
        [
            new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, false, TrancheDue.AtSigning),
            new TrancheDefinition("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ];

        Should.Throw<InvalidOperationException>(
            () => TrancheCalculator.Calculate(rule, 100_000m, 5_000m, false));
    }

    [Fact]
    public void Refuses_a_percentage_tranche_with_no_percentage()
    {
        var rule = RokaRule();
        rule.Tranches =
        [
            new TrancheDefinition("Transa 1", TrancheBasis.Percentage, true, TrancheDue.AtSigning),
            new TrancheDefinition("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ];

        Should.Throw<InvalidOperationException>(
            () => TrancheCalculator.Calculate(rule, 100_000m, 0m, false));
    }

    [Fact]
    public void Refuses_a_rule_with_no_tranches()
    {
        var rule = RokaRule();
        rule.Tranches = [];

        Should.Throw<InvalidOperationException>(
            () => TrancheCalculator.Calculate(rule, 100_000m, 0m, false));
    }
}
