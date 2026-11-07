using Keystone.Domain.Entities;

namespace Keystone.Domain.Pricing;

/// <summary>
/// Checks a pricing rule before it is saved, so that a bad one is refused rather than discovered.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TrancheCalculator"/> already enforces most of this, but only at the moment somebody
/// reads a price — which is a buyer looking at a page, or a reservation being written. Every one
/// of those failures is an exception thrown deep in a request, long after the person who caused it
/// has gone home. The same rules checked at the point of editing become a sentence next to the
/// field that is wrong.
/// </para>
/// <para>
/// Two of the rules here are stricter than the calculator, and deliberately so. The calculator
/// tolerates a non-final <see cref="TrancheBasis.Remainder"/> and tolerates several tranches
/// claiming the cesiune, because in both cases it has a defensible thing to do — consume the rest,
/// and let the first claimant win. Neither is ever what somebody meant.
/// </para>
/// </remarks>
public static class PricingRuleValidator
{
    /// <summary>
    /// Everything wrong with this rule, in the order somebody would want to fix it.
    /// An empty list means it is safe to save.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        decimal advancePct,
        decimal vatStandard,
        decimal cesiuneVatPct,
        decimal? vatReduced,
        IReadOnlyList<TrancheDefinition> tranches)
    {
        var problems = new List<string>();

        Rate(problems, advancePct, "The advance");
        Rate(problems, vatStandard, "The standard VAT rate");
        Rate(problems, cesiuneVatPct, "The VAT rate on the cesiune");

        if (vatReduced is { } reduced)
        {
            Rate(problems, reduced, "The reduced VAT rate");

            if (reduced > vatStandard)
            {
                problems.Add("The reduced VAT rate is higher than the standard one.");
            }
        }

        if (tranches.Count == 0)
        {
            problems.Add("A payment plan needs at least one payment.");
            return problems;
        }

        for (var i = 0; i < tranches.Count; i++)
        {
            var tranche = tranches[i];
            var position = i + 1;

            if (string.IsNullOrWhiteSpace(tranche.Label))
            {
                problems.Add($"Payment {position} has no name.");
            }

            if (tranche.Basis == TrancheBasis.Percentage)
            {
                if (tranche.Percentage is not { } pct)
                {
                    problems.Add($"Payment {position} is a percentage but no percentage was given.");
                }
                else
                {
                    Rate(problems, pct, $"The percentage on payment {position}");
                }
            }

            // A remainder consumes everything left, so anything after it would be nothing at all.
            if (tranche.Basis == TrancheBasis.Remainder && i != tranches.Count - 1)
            {
                problems.Add(
                    $"Payment {position} takes whatever is left, so it has to be the last one.");
            }
        }

        // The calculator forces the final payment to be the remainder whatever its basis says, so
        // a final payment calling itself anything else is describing something it will not do.
        if (tranches[^1].Basis != TrancheBasis.Remainder)
        {
            problems.Add("The last payment has to be the one that takes whatever is left.");
        }

        var claims = tranches.Count(t => t.IncludesCesiune);

        if (claims == 0)
        {
            problems.Add(
                "No payment collects the cesiune. Mark the one it is paid with — usually the first.");
        }
        else if (claims > 1)
        {
            problems.Add(
                "More than one payment collects the cesiune. It is paid once, with a single payment.");
        }

        // Everything before the last is a rate against the developer price. If they reach 100%
        // there is nothing left, and the final payment comes out negative.
        var allocated = 0m;

        for (var i = 0; i < tranches.Count - 1; i++)
        {
            allocated += tranches[i].Basis switch
            {
                TrancheBasis.AdvancePercentage => advancePct,
                TrancheBasis.Percentage => tranches[i].Percentage ?? 0m,
                _ => 0m
            };
        }

        if (allocated >= 1m)
        {
            problems.Add(
                $"The payments before the last one already add up to {allocated:P0} of the price, " +
                "so there would be nothing left for it.");
        }

        return problems;
    }

    /// <summary>A rate is a proportion: something above zero and no more than everything.</summary>
    private static void Rate(List<string> problems, decimal value, string what)
    {
        if (value <= 0m || value > 1m)
        {
            problems.Add($"{what} has to be between 0 and 1 — 0.19 means 19%.");
        }
    }
}
