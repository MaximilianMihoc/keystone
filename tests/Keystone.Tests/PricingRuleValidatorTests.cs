using Keystone.Domain;
using Keystone.Domain.Pricing;
using Shouldly;

namespace Keystone.Tests;

/// <summary>
/// The rules a payment plan has to obey before it can be saved.
/// </summary>
/// <remarks>
/// The shape every real project uses — an advance that also collects the cesiune, then a
/// remainder — has to stay valid. If a change here rejects Roka's plan, the change is wrong.
/// </remarks>
public class PricingRuleValidatorTests
{
    private static TrancheDefinition[] RokaTranches() =>
    [
        new("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
        new("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
    ];

    private static IReadOnlyList<string> Validate(
        TrancheDefinition[] tranches,
        decimal advance = 0.15m,
        decimal vat = 0.19m,
        decimal cesiuneVat = 0.19m,
        decimal? reduced = 0.09m) =>
        PricingRuleValidator.Validate(advance, vat, cesiuneVat, reduced, tranches);

    [Fact]
    public void The_shape_every_real_project_uses_is_valid() =>
        Validate(RokaTranches()).ShouldBeEmpty();

    [Fact]
    public void A_three_payment_plan_with_an_interim_percentage_is_valid() =>
        Validate([
            new("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new("Transa 2", TrancheBasis.Percentage, false, TrancheDue.Interim, 0.30m),
            new("Transa 3", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]).ShouldBeEmpty();

    [Fact]
    public void A_plan_needs_a_payment() =>
        Validate([]).ShouldContain(p => p.Contains("at least one payment"));

    [Fact]
    public void A_percentage_payment_needs_a_percentage() =>
        Validate([
            new("Transa 1", TrancheBasis.Percentage, true, TrancheDue.AtSigning),
            new("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]).ShouldContain(p => p.Contains("no percentage was given"));

    [Fact]
    public void Something_has_to_collect_the_cesiune() =>
        Validate([
            new("Transa 1", TrancheBasis.AdvancePercentage, false, TrancheDue.AtSigning),
            new("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]).ShouldContain(p => p.Contains("No payment collects the cesiune"));

    [Fact]
    public void Only_one_thing_may_collect_the_cesiune() =>
        // The calculator lets the first claimant win rather than failing, so without this the
        // mistake is invisible: the second tranche simply collects nothing.
        Validate([
            new("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new("Transa 2", TrancheBasis.Remainder, true, TrancheDue.AtDelivery)
        ]).ShouldContain(p => p.Contains("More than one payment collects"));

    [Fact]
    public void A_remainder_has_to_come_last() =>
        // Otherwise everything after it is calculated against nothing.
        Validate([
            new("Transa 1", TrancheBasis.Remainder, true, TrancheDue.AtSigning),
            new("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]).ShouldContain(p => p.Contains("has to be the last one"));

    [Fact]
    public void The_last_payment_has_to_be_the_remainder() =>
        // The calculator forces it regardless, so a last payment calling itself 30% would pay
        // something other than what it says.
        Validate([
            new("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new("Transa 2", TrancheBasis.Percentage, false, TrancheDue.AtDelivery, 0.30m)
        ]).ShouldContain(p => p.Contains("takes whatever is left"));

    [Fact]
    public void Payments_before_the_last_cannot_use_up_the_whole_price() =>
        Validate([
            new("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new("Transa 2", TrancheBasis.Percentage, false, TrancheDue.Interim, 0.90m),
            new("Transa 3", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ], advance: 0.15m).ShouldContain(p => p.Contains("nothing left for it"));

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void A_rate_is_a_proportion(decimal advance) =>
        Validate(RokaTranches(), advance: advance).ShouldContain(p => p.Contains("The advance"));

    [Fact]
    public void The_reduced_rate_cannot_beat_the_standard_one() =>
        Validate(RokaTranches(), vat: 0.09m, reduced: 0.19m)
            .ShouldContain(p => p.Contains("higher than the standard"));

    [Fact]
    public void A_project_with_no_reduced_rate_is_valid() =>
        Validate(RokaTranches(), reduced: null).ShouldBeEmpty();

    [Fact]
    public void A_payment_needs_a_name() =>
        Validate([
            new("  ", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]).ShouldContain(p => p.Contains("has no name"));
}
