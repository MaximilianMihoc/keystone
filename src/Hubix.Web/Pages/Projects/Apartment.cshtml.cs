using System.Globalization;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Hubix.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Pages.Projects;

public class ApartmentModel(HubixDbContext db) : PageModel
{
    public string Slug { get; private set; } = "";

    public string ProjectName { get; private set; } = "";

    public int HoldDurationHours { get; private set; }

    public Unit Unit { get; private set; } = null!;

    public string TypeDisplay { get; private set; } = "";

    /// <summary>
    /// The schedule an individual buyer would pay. Individual rather than company because that
    /// is the common case; the buyer type is confirmed at reservation, which is where the price
    /// gets snapshotted.
    /// </summary>
    public PriceBreakdown? Schedule { get; private set; }

    public bool IsFree => Display.IsFree(Unit.State);

    public async Task<IActionResult> OnGetAsync(string slug, string code, CancellationToken ct)
    {
        var culture = CultureInfo.CurrentUICulture.Name;

        var project = await db.Projects
            .AsNoTracking()
            .Where(p => p.Slug == slug && p.PublishedAt != null)
            .Select(p => new { p.Id, p.Name, p.Slug, p.HoldDurationHours })
            .FirstOrDefaultAsync(ct);

        if (project is null)
        {
            return NotFound();
        }

        var unit = await db.Units
            .AsNoTracking()
            .Include(u => u.UnitType!).ThenInclude(t => t.Translations)
            .FirstOrDefaultAsync(
                u => u.ProjectId == project.Id
                     && u.Code == code
                     && u.State != UnitState.Withdrawn, ct);

        if (unit is null)
        {
            return NotFound();
        }

        Slug = project.Slug;
        ProjectName = project.Name;
        HoldDurationHours = project.HoldDurationHours;
        Unit = unit;
        TypeDisplay = unit.UnitType?.DisplayName(culture) ?? "";

        var rule = await db.PricingRules
            .AsNoTracking()
            .Where(r => r.ProjectId == project.Id)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

        if (rule is not null)
        {
            Schedule = TrancheCalculator.Calculate(
                rule,
                unit.BasePriceNet,
                unit.CesiunePriceNet ?? 0m,

                // Shown at the standard rate. The reduced rate depends on the buyer as well as
                // the unit, so it cannot honestly be assumed on a public page.
                reducedVatApplies: false);
        }

        return Page();
    }
}
