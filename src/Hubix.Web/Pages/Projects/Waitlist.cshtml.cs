using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Sales;
using Hubix.Web.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Pages.Projects;

/// <summary>
/// Joining the queue for an apartment type.
/// </summary>
/// <remarks>
/// Replaces a separate Google Form per apartment type. One form, and the type is a field on
/// it — which is also what makes "I'll take whatever comes up" expressible at all.
/// </remarks>
[EnableRateLimiting("reserve")]
public class WaitlistModel(
    HubixDbContext db,
    WaitlistService waitlist,
    HoldNotifier notifier,
    ILogger<WaitlistModel> logger) : PageModel
{
    public string Slug { get; private set; } = "";

    public string ProjectName { get; private set; } = "";

    public Guid ProjectId { get; private set; }

    public int FreeUnits { get; private set; }

    /// <summary>
    /// The types on offer. The canonical name is what goes in the query string — stable and
    /// language-independent — while the label is what the visitor reads.
    /// </summary>
    public IReadOnlyList<TypeChoice> Types { get; private set; } = [];

    public string? FailureKey { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        /// <summary>The canonical type name, or empty for "any apartment".</summary>
        [BindProperty(SupportsGet = true)]
        public string? Type { get; set; }

        [Required(ErrorMessage = "Validation.NameRequired")]
        [StringLength(200)]
        public string? FullName { get; set; }

        [Required(ErrorMessage = "Validation.EmailRequired")]
        [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
        [StringLength(320)]
        public string? Email { get; set; }

        [Phone(ErrorMessage = "Validation.PhoneInvalid")]
        [StringLength(50)]
        public string? Phone { get; set; }

        [Range(typeof(bool), "true", "true", ErrorMessage = "Validation.ConsentRequired")]
        public bool Consent { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string slug, string? type, CancellationToken ct)
    {
        if (!await LoadAsync(slug, ct))
        {
            return NotFound();
        }

        // Arriving from a filtered apartment list should keep the filter.
        Input.Type = Types.Any(t => t.Canonical == type) ? type : null;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        if (!await LoadAsync(slug, ct))
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var chosen = Types.FirstOrDefault(t => t.Canonical == Input.Type);

        var outcome = await waitlist.JoinAsync(
            ProjectId,
            chosen?.Id,
            new ProspectDetails(
                Email: Input.Email!,
                FullName: Input.FullName,
                Phone: Input.Phone,
                Locale: CultureInfo.CurrentUICulture.Name,
                ConsentGiven: Input.Consent,
                ConsentSource: $"waitlist-form/{slug}"),
            ct);

        switch (outcome)
        {
            case WaitlistOutcome.Joined joined:
                await ConfirmAsync(joined.AccessToken, chosen?.Label, joined.Place, ct);
                return RedirectToPage("/Waitlist", new { token = joined.AccessToken });

            // Somebody who submits the form twice gets shown where they already stand, not
            // told off for it.
            case WaitlistOutcome.AlreadyWaiting already:
                return RedirectToPage("/Waitlist", new { token = already.AccessToken });

            default:
                FailureKey = "Waitlist.CouldNotJoin";
                return Page();
        }
    }

    /// <summary>
    /// The entry is already saved by this point. A confirmation that fails to send must not
    /// undo it or show an error for something that worked.
    /// </summary>
    private async Task ConfirmAsync(string token, string? typeLabel, int place, CancellationToken ct)
    {
        try
        {
            await notifier.WaitlistJoinedAsync(
                Input.Email!, CultureInfo.CurrentUICulture.Name, ProjectName,
                typeLabel, place, token, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Somebody joined the waiting list for {Project} but the confirmation failed",
                ProjectName);
        }
    }

    private async Task<bool> LoadAsync(string slug, CancellationToken ct)
    {
        var culture = CultureInfo.CurrentUICulture.Name;

        var project = await db.Projects
            .AsNoTracking()
            .Where(p => p.Slug == slug && p.PublishedAt != null)
            .Select(p => new { p.Id, p.Name, p.Slug })
            .FirstOrDefaultAsync(ct);

        if (project is null)
        {
            return false;
        }

        ProjectId = project.Id;
        ProjectName = project.Name;
        Slug = project.Slug;

        var types = await db.UnitTypes
            .AsNoTracking()
            .Where(t => t.ProjectId == project.Id)
            .OrderBy(t => t.SortOrder)
            .Select(t => new
            {
                t.Id,
                t.Name,
                Translations = t.Translations
                    .Select(x => new { x.Culture, x.DisplayName })
                    .ToList(),
                Free = t.Units.Count(u => u.State == UnitState.Available)
            })
            .ToListAsync(ct);

        Types = types.Select(t => new TypeChoice(
                t.Id,
                t.Name,
                t.Translations
                    .Select(x => new UnitTypeTranslation { Culture = x.Culture, DisplayName = x.DisplayName })
                    .ForCulture(culture, fallBackToAny: false)?.DisplayName ?? t.Name,
                t.Free))
            .ToList();

        FreeUnits = types.Sum(t => t.Free);

        return true;
    }

    public sealed record TypeChoice(Guid Id, string Canonical, string Label, int Free);
}
