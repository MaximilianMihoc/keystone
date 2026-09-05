using Hubix.Domain.Common;
using Hubix.Domain.Pricing;

namespace Hubix.Domain.Entities;

/// <summary>
/// How a project's prices break into payments. Versioned, so a rule change does not rewrite
/// the terms of reservations already agreed.
/// </summary>
/// <remarks>
/// Across the five spreadsheets this replaces, advances run 12% to 27% and VAT is 9%, 19% or
/// 21%. Those are not five schemas; they are one rule with different parameters.
/// </remarks>
public class PricingRule : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>Advance as a fraction: 0.1400, 0.1820, 0.2700.</summary>
    public decimal AdvancePct { get; set; }

    /// <summary>VAT charged on the assignment price: 0.1900 or 0.2100.</summary>
    public decimal CesiuneVatPct { get; set; }

    /// <summary>Standard VAT on the apartment: 0.1900 or 0.2100.</summary>
    public decimal VatStandard { get; set; }

    /// <summary>Reduced VAT where a buyer and unit both qualify, typically 0.0900.</summary>
    public decimal? VatReduced { get; set; }

    /// <summary>
    /// The payment schedule, stored as JSON. A list rather than columns, so a project
    /// paying in two instalments and one paying in three share a schema.
    /// </summary>
    public IReadOnlyList<TrancheDefinition> Tranches { get; set; } = [];

    public DateTimeOffset EffectiveFrom { get; set; } = DateTimeOffset.UtcNow;
}
