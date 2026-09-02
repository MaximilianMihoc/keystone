using System.Globalization;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Assets;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Pages.Projects;

public class ApartmentModel(
    HubixDbContext db,
    AssetService assets,
    IStringLocalizer<SharedResource> localizer) : PageModel
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

    /// <summary>
    /// Drawings for this apartment: its own RLV and renders, plus the plan for its floor.
    /// </summary>
    /// <remarks>
    /// The floor plan is not attached to the unit. It belongs to the floor, and every
    /// apartment on that floor picks it up — which is the whole reason floors are records.
    /// In the spreadsheet a Drive link is pasted into each row, and some rows have the wrong
    /// one.
    /// </remarks>
    public IReadOnlyList<Drawing> Drawings { get; private set; } = [];

    public sealed record Drawing(string Title, string FileName, string Url, bool IsImage);

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

        Drawings = await LoadDrawingsAsync(project.Id, unit, ct);

        return Page();
    }

    private async Task<IReadOnlyList<Drawing>> LoadDrawingsAsync(
        Guid projectId,
        Unit unit,
        CancellationToken ct)
    {
        var attached = await db.UnitAssets
            .AsNoTracking()
            .Where(ua => ua.UnitId == unit.Id)
            .OrderBy(ua => ua.SortOrder)
            .Select(ua => new { ua.Asset!.Kind, ua.Asset.FileName, ua.Asset.StorageKey, ua.Asset.Caption })
            .ToListAsync(ct);

        var floorPlan = unit.FloorLevel is { } level
            ? await db.Floors
                .AsNoTracking()
                .Where(f => f.ProjectId == projectId
                            && f.Building == unit.Building
                            && f.Level == level
                            && f.FloorPlanAsset != null)
                .Select(f => new { f.FloorPlanAsset!.FileName, f.FloorPlanAsset.StorageKey })
                .FirstOrDefaultAsync(ct)
            : null;

        var drawings = new List<Drawing>();

        foreach (var item in attached)
        {
            drawings.Add(new Drawing(
                item.Caption ?? Title(item.Kind),
                item.FileName,
                await assets.LinkAsync(item.StorageKey, ct),
                IsImage(item.FileName)));
        }

        if (floorPlan is not null)
        {
            drawings.Add(new Drawing(
                localizer["Asset.FloorPlan"].Value,
                floorPlan.FileName,
                await assets.LinkAsync(floorPlan.StorageKey, ct),
                IsImage(floorPlan.FileName)));
        }

        return drawings;
    }

    private string Title(AssetKind kind) => kind switch
    {
        AssetKind.Rlv => localizer["Asset.Rlv"].Value,
        AssetKind.Render => localizer["Asset.Render"].Value,
        AssetKind.FloorPlan => localizer["Asset.FloorPlan"].Value,
        _ => localizer["Asset.Document"].Value
    };

    private static bool IsImage(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".svg";
}
