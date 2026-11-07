using Keystone.Domain.Entities;

namespace Keystone.Domain.Pricing;

/// <summary>
/// Turns a project's pricing rule plus a unit's price into the tranche schedule a buyer pays.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that tranche amounts are <em>derived, never entered</em>. In the spreadsheets
/// this replaces, every tranche is a hand-typed column, which is why the five projects have five
/// different layouts and why some rows have lost their formula.
/// </para>
/// <para>
/// All money is <see cref="decimal"/>. Never <see cref="double"/>, anywhere, for any reason.
/// </para>
/// </remarks>
public static class TrancheCalculator
{
    /// <summary>
    /// Calculate the schedule for one unit and one buyer.
    /// </summary>
    /// <param name="rule">The project's pricing rule.</param>
    /// <param name="basePriceNet">
    /// Everything the buyer pays, net of VAT — Hubix's "Pret total apartament fara TVA".
    /// </param>
    /// <param name="cesiuneNet">
    /// The assignment price, excluding VAT — the margin Hubix negotiated. This sits <em>inside</em>
    /// the base price rather than on top of it, so the developer receives
    /// <c>basePriceNet - cesiuneNet</c>, and that is what percentage tranches are measured against.
    /// Verified to the cent against the Roka (15%) and Torontal (20%) spreadsheets.
    /// </param>
    /// <param name="reducedVatApplies">
    /// Whether this buyer qualifies for the reduced rate on this unit. Depends on both the unit's
    /// eligibility and the buyer, so it cannot be resolved from the unit alone.
    /// </param>
    public static PriceBreakdown Calculate(
        PricingRule rule,
        decimal basePriceNet,
        decimal cesiuneNet,
        bool reducedVatApplies)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentOutOfRangeException.ThrowIfNegative(basePriceNet);
        ArgumentOutOfRangeException.ThrowIfNegative(cesiuneNet);

        var definitions = rule.Tranches;
        if (definitions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Pricing rule {rule.Id} has no tranches defined.");
        }

        if (cesiuneNet > basePriceNet)
        {
            throw new InvalidOperationException(
                "The cesiune price cannot exceed the total price; it is part of it, not additional.");
        }

        var vatRate = reducedVatApplies && rule.VatReduced.HasValue
            ? rule.VatReduced.Value
            : rule.VatStandard;

        // What actually reaches the developer. Percentage tranches are measured against this.
        var developerPriceNet = basePriceNet - cesiuneNet;

        // The assignment price is collected once, with whichever tranche claims it.
        var cesiuneClaimed = false;

        var results = new List<TrancheAmount>(definitions.Count);
        var apartmentAllocated = 0m;

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var isLast = i == definitions.Count - 1;

            var apartmentNet = definition.Basis switch
            {
                TrancheBasis.AdvancePercentage => Round(developerPriceNet * rule.AdvancePct),

                TrancheBasis.Percentage => Round(developerPriceNet * (definition.Percentage
                    ?? throw new InvalidOperationException(
                        $"Tranche '{definition.Label}' uses Percentage but none was given."))),

                TrancheBasis.Remainder => developerPriceNet - apartmentAllocated,

                _ => throw new ArgumentOutOfRangeException(nameof(rule))
            };

            // The final tranche absorbs any rounding residue, so the schedule always sums
            // exactly to the unit price rather than being a penny out.
            if (isLast)
            {
                apartmentNet = developerPriceNet - apartmentAllocated;
            }

            apartmentAllocated += apartmentNet;

            // The rate that describes the amount, for a page that shows the breakdown. The
            // last tranche is whatever is left after the others, whatever basis it names, so
            // no rate honestly describes it.
            var rate = isLast ? null : definition.Basis switch
            {
                TrancheBasis.AdvancePercentage => rule.AdvancePct,
                TrancheBasis.Percentage => definition.Percentage,
                _ => null
            };

            var trancheCesiuneNet = 0m;
            if (definition.IncludesCesiune && !cesiuneClaimed)
            {
                trancheCesiuneNet = cesiuneNet;
                cesiuneClaimed = true;
            }

            results.Add(new TrancheAmount(
                Label: definition.Label,
                Due: definition.Due,
                ApartmentNet: apartmentNet,
                ApartmentVat: Round(apartmentNet * vatRate),
                CesiuneNet: trancheCesiuneNet,
                CesiuneVat: Round(trancheCesiuneNet * rule.CesiuneVatPct),
                Percentage: rate));
        }

        if (cesiuneNet > 0 && !cesiuneClaimed)
        {
            throw new InvalidOperationException(
                $"Pricing rule {rule.Id} has a cesiune price but no tranche collects it. " +
                "Set IncludesCesiune on exactly one tranche.");
        }

        return new PriceBreakdown(
            BasePriceNet: basePriceNet,
            CesiuneNet: cesiuneNet,
            DeveloperPriceNet: developerPriceNet,
            VatRate: vatRate,
            CesiuneVatRate: rule.CesiuneVatPct,
            Tranches: results);
    }

    /// <summary>
    /// Banker's rounding is the wrong choice for prices a buyer reads on an invoice;
    /// away-from-zero matches what a person expects and what the spreadsheets do.
    /// </summary>
    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
