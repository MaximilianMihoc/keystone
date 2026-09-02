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

[EnableRateLimiting("reserve")]
public class ReserveModel(
    HubixDbContext db,
    HoldService holds,
    HoldNotifier notifier,
    ILogger<ReserveModel> logger) : PageModel
{
    public string Slug { get; private set; } = "";

    public string ProjectName { get; private set; } = "";

    public string UnitCode { get; private set; } = "";

    public Guid UnitId { get; private set; }

    public int HoldDurationHours { get; private set; }

    public decimal? ReservationFee { get; private set; }

    /// <summary>Set when the attempt failed, so the page can explain rather than just fail.</summary>
    public string? FailureKey { get; private set; }

    public object[] FailureArgs { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
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

    public async Task<IActionResult> OnGetAsync(string slug, string code, CancellationToken ct)
    {
        var loaded = await LoadAsync(slug, code, ct);
        if (!loaded)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, string code, CancellationToken ct)
    {
        var loaded = await LoadAsync(slug, code, ct);
        if (!loaded)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var outcome = await holds.TryHoldAsync(
            UnitId,
            new ProspectDetails(
                Email: Input.Email!,
                FullName: Input.FullName,
                Phone: Input.Phone,
                Locale: CultureInfo.CurrentUICulture.Name,
                ConsentGiven: Input.Consent,
                ConsentSource: $"reserve-form/{slug}/{code}"),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);

        switch (outcome)
        {
            case HoldOutcome.Created created:
                await SendConfirmationsAsync(created, ct);
                return RedirectToPage("/Hold", new { token = created.AccessToken });

            case HoldOutcome.NoLongerAvailable:
                FailureKey = "Reserve.LostTheRace";
                return Page();

            case HoldOutcome.ProspectBlocked:
                FailureKey = "Reserve.Blocked";
                return Page();

            case HoldOutcome.TooManyHolds tooMany:
                FailureKey = "Reserve.TooMany";
                FailureArgs = [tooMany.Limit];
                return Page();

            default:
                FailureKey = "Reserve.LostTheRace";
                return Page();
        }
    }

    /// <summary>
    /// The hold is already committed by this point. An email that fails to send must not
    /// undo it or show the prospect an error for something that actually worked.
    /// </summary>
    private async Task SendConfirmationsAsync(HoldOutcome.Created created, CancellationToken ct)
    {
        try
        {
            await notifier.HoldConfirmedAsync(
                Input.Email!, CultureInfo.CurrentUICulture.Name, ProjectName, UnitCode,
                created.ExpiresAt, HoldDurationHours, created.AccessToken, ct);

            await notifier.SalesNotifiedAsync(
                ProjectName, UnitCode, Input.Email!, Input.FullName, Input.Phone,
                created.ExpiresAt, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Hold {HoldId} was created but its confirmation email failed", created.HoldId);
        }
    }

    private async Task<bool> LoadAsync(string slug, string code, CancellationToken ct)
    {
        var found = await db.Units
            .AsNoTracking()
            .Where(u => u.Code == code
                        && u.Project!.Slug == slug
                        && u.Project.PublishedAt != null)
            .Select(u => new
            {
                u.Id,
                u.Code,
                u.State,
                ProjectName = u.Project!.Name,
                ProjectSlug = u.Project.Slug,
                u.Project.HoldDurationHours,
                u.Project.ReservationFee
            })
            .FirstOrDefaultAsync(ct);

        if (found is null)
        {
            return false;
        }

        Slug = found.ProjectSlug;
        ProjectName = found.ProjectName;
        UnitCode = found.Code;
        UnitId = found.Id;
        HoldDurationHours = found.HoldDurationHours;
        ReservationFee = found.ReservationFee;

        // Show the form only for a unit that is genuinely free. Anything else and the page
        // explains why instead, rather than collecting details for a request that cannot win.
        if (found.State != UnitState.Available)
        {
            FailureKey = "Reserve.LostTheRace";
        }

        return true;
    }

    public bool CanSubmit => FailureKey is null;
}
