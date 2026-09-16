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

    /// <summary>Arriving back from a successful post: the link is on its way.</summary>
    public bool Sent { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        /// <summary>The canonical type name, or empty for "any apartment".</summary>
        [BindProperty(SupportsGet = true)]
        public string? Type { get; set; }

        [Required(ErrorMessage = "Validation.FirstNameRequired")]
        [StringLength(100)]
        public string? FirstName { get; set; }

        [Required(ErrorMessage = "Validation.SurnameRequired")]
        [StringLength(100)]
        public string? Surname { get; set; }

        [Required(ErrorMessage = "Validation.EmailRequired")]
        [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
        [StringLength(320)]
        public string? Email { get; set; }

        [Required(ErrorMessage = "Validation.PhoneRequired")]
        [Phone(ErrorMessage = "Validation.PhoneInvalid")]
        [StringLength(50)]
        public string? Phone { get; set; }

        [Required(ErrorMessage = "Validation.CityRequired")]
        [StringLength(120)]
        public string? City { get; set; }

        /// <summary>Blank means Romania. See the same field on the reservation form.</summary>
        [StringLength(100)]
        public string? Country { get; set; }

        [Range(typeof(bool), "true", "true", ErrorMessage = "Validation.ConsentRequired")]
        public bool Consent { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string slug, string? type, bool sent, CancellationToken ct)
    {
        if (!await LoadAsync(slug, ct))
        {
            return NotFound();
        }

        Sent = sent;

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
                FirstName: Input.FirstName,
                Surname: Input.Surname,
                Phone: Input.Phone,
                City: Input.City,
                Country: string.IsNullOrWhiteSpace(Input.Country) ? null : Input.Country.Trim(),
                Locale: CultureInfo.CurrentUICulture.Name,
                ConsentGiven: Input.Consent,
                ConsentSource: $"waitlist-form/{slug}"),
            ct);

        // Joining and already being on the list look exactly the same from here: the same
        // email, the same page. Whoever submitted the form need not own the address, so the
        // answer must neither hand over the entry's link nor say whether the address was
        // already on the list (issue #29). The link reaches only the inbox, and somebody who
        // submits twice simply gets it again.
        switch (outcome)
        {
            case WaitlistOutcome.Joined joined:
                await ConfirmAsync(joined.EntryId, chosen?.Label, joined.Place, ct);
                return RedirectToPage(new { slug = Slug, sent = true });

            case WaitlistOutcome.AlreadyWaiting already:
                await ConfirmAsync(already.EntryId, chosen?.Label, already.Place, ct);
                return RedirectToPage(new { slug = Slug, sent = true });

            default:
                FailureKey = "Waitlist.CouldNotJoin";
                return Page();
        }
    }

    /// <summary>
    /// The entry is already saved by this point. A confirmation that fails to send must not
    /// undo it or show an error for something that worked.
    /// </summary>
    private async Task ConfirmAsync(Guid entryId, string? typeLabel, int place, CancellationToken ct)
    {
        try
        {
            await notifier.WaitlistJoinedAsync(
                entryId, CultureInfo.CurrentUICulture.Name, ProjectId, ProjectName,
                typeLabel, place, ct);
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
            .OrderBy(t => t.SortOrder)
            .Select(t => new
            {
                t.Id,
                t.Name,
                Translations = t.Translations
                    .Select(x => new { x.Culture, x.DisplayName })
                    .ToList(),
                // The catalogue is portal-wide, so this has to say "free here", not "free".
                Free = t.Units.Count(u => u.ProjectId == project.Id && u.State == UnitState.Available)
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
