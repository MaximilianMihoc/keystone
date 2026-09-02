using System.Globalization;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Pages.Projects;

public class ApartmentsModel(HubixDbContext db) : PageModel
{
    public string Slug { get; private set; } = "";

    public string ProjectName { get; private set; } = "";

    public int HoldDurationHours { get; private set; }

    /// <summary>Every unit, for the elevation board — which shows the whole building.</summary>
    public IReadOnlyList<UnitRow> AllUnits { get; private set; } = [];

    /// <summary>Units after filtering, for the table.</summary>
    public IReadOnlyList<UnitRow> Rows { get; private set; } = [];

    /// <summary>Floors descending, so the board reads like an elevation.</summary>
    public IReadOnlyList<FloorGroup> Floors { get; private set; } = [];

    /// <summary>
    /// Filter options. The canonical name is what goes in the query string — it is stable and
    /// language-independent — while the label is what the visitor reads.
    /// </summary>
    public IReadOnlyList<TypeOption> Types { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Type { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool FreeOnly { get; set; }

    public int TotalUnits => AllUnits.Count;

    public int FreeUnits => AllUnits.Count(u => Display.IsFree(u.State));

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
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

        Slug = project.Slug;
        ProjectName = project.Name;
        HoldDurationHours = project.HoldDurationHours;

        var units = await db.Units
            .AsNoTracking()
            .Where(u => u.ProjectId == project.Id && u.State != UnitState.Withdrawn)
            .OrderBy(u => u.FloorLevel).ThenBy(u => u.Code)
            .Select(u => new
            {
                u.Code,
                u.FloorLevel,
                u.Orientation,
                u.AreaUsable,
                u.AreaBalcony,
                u.AreaTotal,
                u.BasePriceNet,
                u.State,
                TypeName = u.UnitType!.Name,
                TypeTranslations = u.UnitType.Translations
                    .Select(t => new { t.Culture, t.DisplayName })
                    .ToList()
            })
            .ToListAsync(ct);

        AllUnits = units.Select(u => new UnitRow(
            u.Code,
            u.FloorLevel,
            u.Orientation,
            u.AreaUsable,
            u.AreaBalcony,
            u.AreaTotal,
            u.BasePriceNet,
            u.State,
            u.TypeTranslations
                .Select(t => new UnitTypeTranslation { Culture = t.Culture, DisplayName = t.DisplayName })
                .ForCulture(culture, fallBackToAny: false)?.DisplayName ?? u.TypeName,
            u.TypeName))
            .ToList();

        Types = AllUnits
            .GroupBy(u => u.CanonicalType)
            .OrderBy(g => g.Key)
            .Select(g => new TypeOption(g.Key, g.First().TypeDisplay))
            .ToList();

        Rows = AllUnits
            .Where(u => Type is null || u.CanonicalType == Type)
            .Where(u => !FreeOnly || Display.IsFree(u.State))
            .ToList();

        // Descending: floor 8 sits above floor 1, as it does in the building.
        Floors = AllUnits
            .GroupBy(u => u.FloorLevel)
            .OrderByDescending(g => g.Key ?? int.MinValue)
            .Select(g => new FloorGroup(g.Key, g.OrderBy(u => u.Code).ToList()))
            .ToList();

        return Page();
    }

    public sealed record UnitRow(
        string Code,
        int? FloorLevel,
        string? Orientation,
        decimal AreaUsable,
        decimal AreaBalcony,
        decimal AreaTotal,
        decimal BasePriceNet,
        UnitState State,
        string TypeDisplay,
        string CanonicalType);

    public sealed record FloorGroup(int? Level, IReadOnlyList<UnitRow> Units);

    public sealed record TypeOption(string Canonical, string Label);
}
