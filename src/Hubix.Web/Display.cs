using System.Globalization;
using Hubix.Domain;
using Hubix.Domain.Pricing;

namespace Hubix.Web;

/// <summary>
/// Formatting shared by the public pages.
/// </summary>
/// <remarks>
/// Money is stored culture-invariant and only formatted here. Romanian inverts the separators
/// (134.859,00 against 134,859.00), so this must never be done by string concatenation.
/// </remarks>
public static class Display
{
    /// <summary>A price, in whole euro. Cents are noise at these amounts.</summary>
    public static string Money(decimal value) =>
        "€" + value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A price with cents, for tranche lines where the total must reconcile.</summary>
    public static string MoneyExact(decimal value) =>
        "€" + value.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>Square metres, as Hubix writes them.</summary>
    public static string Area(decimal value) =>
        value.ToString("N2", CultureInfo.CurrentCulture) + " mp";

    /// <summary>Resource key for a unit state, so states are translated not stringified.</summary>
    public static string StateKey(UnitState state) => $"State.{state}";

    /// <summary>Resource key for when a tranche falls due.</summary>
    public static string DueKey(TrancheDue due) => due switch
    {
        TrancheDue.AtSigning => "Tranche.DueAtSigning",
        TrancheDue.Interim => "Tranche.DueInterim",
        TrancheDue.AtDelivery => "Tranche.DueAtDelivery",
        _ => "Tranche.DueInterim"
    };

    /// <summary>
    /// Only <see cref="UnitState.Available"/> is offered to the public as free. A held unit is
    /// somebody else's for the next two days, and showing it as available would invite a race
    /// the buyer always loses.
    /// </summary>
    public static bool IsFree(UnitState state) => state is UnitState.Available;
}
