using System.Globalization;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Pages.Projects;

public class DetailModel(HubixDbContext db) : PageModel
{
    public Project Project { get; private set; } = null!;

    public ProjectTranslation? Text { get; private set; }

    /// <summary>The city in the visitor's language, or Hubix's own spelling of it.</summary>
    public string City { get; private set; } = "";

    public string? DeveloperName { get; private set; }

    public int TotalUnits { get; private set; }

    public int FreeUnits { get; private set; }

    public decimal? FromPrice { get; private set; }

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var project = await db.Projects
            .AsNoTracking()
            .Include(p => p.Developer)
            .Include(p => p.Translations)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.PublishedAt != null, ct);

        if (project is null)
        {
            return NotFound();
        }

        Project = project;
        DeveloperName = project.Developer?.Name;

        // Prose falls back to Romanian when no English translation exists — Hubix authors
        // in Romanian, and Romanian beats a blank panel.
        Text = project.Translations.ForCulture(CultureInfo.CurrentUICulture.Name);
        City = project.Translations.CityFor(CultureInfo.CurrentUICulture.Name, project.City);

        var counts = await db.Units
            .AsNoTracking()
            .Where(u => u.ProjectId == project.Id && u.State != UnitState.Withdrawn)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Free = g.Count(u => u.State == UnitState.Available),
                From = g.Where(u => u.State == UnitState.Available)
                        .Min(u => (decimal?)u.BasePriceNet)
            })
            .FirstOrDefaultAsync(ct);

        TotalUnits = counts?.Total ?? 0;
        FreeUnits = counts?.Free ?? 0;
        FromPrice = counts?.From;

        return Page();
    }

    /// <summary>
    /// True when the prose shown is not in the language the visitor asked for, so the page can
    /// say so rather than silently serving Romanian to an English reader.
    /// </summary>
    public bool TextIsFallback =>
        Text is not null
        && !Text.Culture.StartsWith(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            StringComparison.OrdinalIgnoreCase);
}
