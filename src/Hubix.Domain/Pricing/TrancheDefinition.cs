namespace Hubix.Domain.Pricing;

/// <summary>
/// One payment in a project's schedule.
/// </summary>
/// <param name="Label">
/// Hubix's own name for it, kept for the spreadsheet export, which mirrors their column
/// headings. Not what the public pages show — those number the payments by position, so the
/// label can be read in the visitor's language.
/// </param>
/// <param name="Basis">How the apartment portion is calculated.</param>
/// <param name="IncludesCesiune">
/// Whether the assignment price is collected with this tranche. True for the first payment
/// on every project seen so far.
/// </param>
/// <param name="Due">When it falls due.</param>
/// <param name="Percentage">
/// Required when <paramref name="Basis"/> is <see cref="TrancheBasis.Percentage"/>; ignored otherwise.
/// </param>
public sealed record TrancheDefinition(
    string Label,
    TrancheBasis Basis,
    bool IncludesCesiune,
    TrancheDue Due,
    decimal? Percentage = null);

/// <summary>A calculated tranche, in money.</summary>
/// <param name="Percentage">
/// The rate the apartment portion was calculated at, or null for a remainder. Carried so
/// a page showing the breakdown can name the rate without also holding the pricing rule.
/// </param>
public sealed record TrancheAmount(
    string Label,
    TrancheDue Due,
    decimal ApartmentNet,
    decimal ApartmentVat,
    decimal CesiuneNet,
    decimal CesiuneVat,
    decimal? Percentage = null)
{
    public decimal Net => ApartmentNet + CesiuneNet;

    public decimal Vat => ApartmentVat + CesiuneVat;

    public decimal Gross => Net + Vat;
}

/// <summary>The full price of one unit for one buyer, broken into tranches.</summary>
/// <param name="BasePriceNet">Everything the buyer pays, net of VAT.</param>
/// <param name="CesiuneNet">Hubix's assignment fee. Part of the base price, not added to it.</param>
/// <param name="DeveloperPriceNet">
/// What reaches the developer: <c>BasePriceNet - CesiuneNet</c>. Percentage-based tranches are
/// calculated against this, not against the base price — verified against the Roka and Torontal
/// spreadsheets, which agree to the cent.
/// </param>
public sealed record PriceBreakdown(
    decimal BasePriceNet,
    decimal CesiuneNet,
    decimal DeveloperPriceNet,
    decimal VatRate,
    decimal CesiuneVatRate,
    IReadOnlyList<TrancheAmount> Tranches)
{
    public decimal TotalNet => Tranches.Sum(t => t.Net);

    public decimal TotalVat => Tranches.Sum(t => t.Vat);

    public decimal TotalGross => Tranches.Sum(t => t.Gross);
}
