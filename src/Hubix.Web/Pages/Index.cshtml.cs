using System.Globalization;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Pages;

public class IndexModel(HubixDbContext db) : PageModel
{
    public IReadOnlyList<ProjectRow> Projects { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var culture = CultureInfo.CurrentUICulture.Name;

        // Read-only page: no change tracking, and project straight to what is rendered
        // rather than loading whole aggregates.
        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => p.PublishedAt != null && p.Status != ProjectStatus.Archived)
            .OrderBy(p => p.Status == ProjectStatus.SoldOut)
            .ThenBy(p => p.DeliveryDate)
            .Select(p => new
            {
                p.Slug,
                p.Name,
                p.City,
                p.Status,
                p.UnitsCommitted,
                p.DeliveryDate,
                p.ReservationFee,
                DeveloperName = p.Developer!.Name,
                Total = p.Units.Count,
                Free = p.Units.Count(u => u.State == UnitState.Available),
                FromPrice = p.Units
                    .Where(u => u.State == UnitState.Available)
                    .Min(u => (decimal?)u.BasePriceNet),
                Translations = p.Translations
                    .Select(t => new { t.Culture, t.Summary })
                    .ToList()
            })
            .ToListAsync(ct);

        Projects = projects.Select(p => new ProjectRow(
            p.Slug,
            p.Name,
            p.City,
            p.Status,
            p.DeveloperName,
            p.DeliveryDate,
            p.Total,
            p.Free,
            p.FromPrice,
            p.Translations
                .Select(t => new ProjectTranslation { Culture = t.Culture, Summary = t.Summary })
                .ForCulture(culture)?.Summary))
            .ToList();
    }

    public sealed record ProjectRow(
        string Slug,
        string Name,
        string City,
        ProjectStatus Status,
        string? Developer,
        DateOnly? DeliveryDate,
        int TotalUnits,
        int FreeUnits,
        decimal? FromPrice,
        string? Summary);
}
