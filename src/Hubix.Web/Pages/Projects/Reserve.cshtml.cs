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
using Microsoft.Extensions.Localization;

namespace Hubix.Web.Pages.Projects;

[EnableRateLimiting("reserve")]
public class ReserveModel(
    HubixDbContext db,
    HoldService holds,
    HoldNotifier notifier,
    IStringLocalizer<SharedResource> localizer,
    ILogger<ReserveModel> logger) : PageModel
{
    public string Slug { get; private set; } = "";

    public string ProjectName { get; private set; } = "";

    public Guid ProjectId { get; private set; }

    public string UnitCode { get; private set; } = "";

    public Guid UnitId { get; private set; }

    public int HoldDurationHours { get; private set; }

    public decimal? ReservationFee { get; private set; }

    /// <summary>How many spaces this project allows with one apartment. Zero offers none.</summary>
    public int MaxParkingSpaces { get; private set; }

    /// <summary>The kinds on offer, priced, with a live count of what is free.</summary>
    public IReadOnlyList<ParkingOffer> ParkingOnOffer { get; private set; } = [];

    public sealed record ParkingOffer(
        ParkingSpaceKind Kind,
        decimal PriceNet,
        decimal DepositNet,
        int Available);

    public bool ShowParking => MaxParkingSpaces > 0 && ParkingOnOffer.Count > 0;

    /// <summary>
    /// Whatever else this project needs a buyer to read before committing, in their language.
    /// Blank on most projects.
    /// </summary>
    public string? Notice { get; private set; }

    /// <summary>Set when the attempt failed, so the page can explain rather than just fail.</summary>
    public string? FailureKey { get; private set; }

    public object[] FailureArgs { get; private set; } = [];

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
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

        /// <summary>
        /// Left blank by almost everybody, and a blank means Romania — which is what the field
        /// says on the form. Only a buyer from somewhere else has anything to type here.
        /// </summary>
        [StringLength(100)]
        public string? Country { get; set; }

        [Range(typeof(bool), "true", "true", ErrorMessage = "Validation.ConsentRequired")]
        public bool Consent { get; set; }

        /// <summary>
        /// The name of a <see cref="ParkingSpaceKind"/>, or empty for no parking, which is the
        /// default and the common case.
        /// </summary>
        public string? ParkingKind { get; set; }

        [Range(0, 10, ErrorMessage = "Validation.ParkingQuantity")]
        public int ParkingQuantity { get; set; } = 1;
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

        // A kind this project does not offer is treated as no request at all rather than as an
        // error: the form only ever posts what it drew, so anything else was not typed by a
        // person filling it in.
        ParkingRequest? parking = null;

        if (ShowParking
            && Input.ParkingQuantity > 0
            && Enum.TryParse<ParkingSpaceKind>(Input.ParkingKind, out var kind)
            && ParkingOnOffer.Any(o => o.Kind == kind))
        {
            parking = new ParkingRequest(kind, Input.ParkingQuantity);
        }

        var outcome = await holds.TryHoldAsync(
            UnitId,
            new ProspectDetails(
                Email: Input.Email!,
                FirstName: Input.FirstName,
                Surname: Input.Surname,
                Phone: Input.Phone,
                City: Input.City,
                Country: Blank(Input.Country),
                Locale: CultureInfo.CurrentUICulture.Name,
                ConsentGiven: Input.Consent,
                ConsentSource: $"reserve-form/{slug}/{code}"),
            parking,
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

            case HoldOutcome.NotEnoughParking shortfall:
                // Nothing was held, the apartment included, so the form comes back with fresh
                // counts and they can choose again.
                await LoadAsync(slug, code, ct);

                // Unless the apartment itself went in the meantime, in which case LoadAsync
                // has already said so and offering the form again would offer nothing.
                CanRetry = FailureKey is null;
                FailureKey = "Reserve.NotEnoughParking";
                FailureArgs =
                [
                    shortfall.Available,
                    localizer[Display.ParkingSpaceKey(shortfall.Kind)].Value,
                    shortfall.Requested
                ];
                return Page();

            case HoldOutcome.TooManyParkingSpaces tooMany2:
                await LoadAsync(slug, code, ct);

                CanRetry = FailureKey is null;
                FailureKey = "Reserve.TooManyParking";
                FailureArgs = [tooMany2.Limit, tooMany2.Requested];
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
                Input.Email!, CultureInfo.CurrentUICulture.Name, ProjectId, ProjectName, UnitCode,
                created.ExpiresAt, HoldDurationHours, created.AccessToken, ct);

            await notifier.SalesNotifiedAsync(
                ProjectId, ProjectName, UnitCode, Input.Email!, FullName, Input.Phone,
                created.ExpiresAt, created.ParkingSpaces, ct);
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
                u.ProjectId,
                ProjectName = u.Project!.Name,
                ProjectSlug = u.Project.Slug,
                u.Project.HoldDurationHours,
                u.Project.ReservationFee,
                u.Project.MaxParkingSpacesPerReservation,
                Notices = u.Project.Translations
                    .Select(t => new { t.Culture, t.ReservationNotice })
                    .ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (found is null)
        {
            return false;
        }

        Slug = found.ProjectSlug;
        ProjectName = found.ProjectName;
        ProjectId = found.ProjectId;
        UnitCode = found.Code;
        UnitId = found.Id;
        HoldDurationHours = found.HoldDurationHours;
        ReservationFee = found.ReservationFee;
        MaxParkingSpaces = found.MaxParkingSpacesPerReservation;

        // Falls back to Romanian rather than to nothing: an untranslated notice still says
        // something a buyer needs before they commit.
        Notice = found.Notices
            .Select(t => new ProjectTranslation { Culture = t.Culture, ReservationNotice = t.ReservationNotice })
            .ReservationNoticeFor(CultureInfo.CurrentUICulture.Name);

        if (MaxParkingSpaces > 0)
        {
            ParkingOnOffer = await LoadParkingAsync(found.ProjectId, ct);
        }

        // Show the form only for a unit that is genuinely free. Anything else and the page
        // explains why instead, rather than collecting details for a request that cannot win.
        if (found.State != UnitState.Available)
        {
            FailureKey = "Reserve.LostTheRace";
        }

        return true;
    }

    /// <summary>
    /// The kinds of space this buyer could actually take: priced, and with at least one free.
    /// </summary>
    /// <remarks>
    /// A count, not a reservation. Between drawing this form and the buyer sending it the last
    /// space can go, which is why the claim is guarded again inside the hold transaction and
    /// why <see cref="HoldOutcome.NotEnoughParking"/> exists.
    /// </remarks>
    private async Task<IReadOnlyList<ParkingOffer>> LoadParkingAsync(Guid projectId, CancellationToken ct)
    {
        var prices = await db.ParkingPrices
            .AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .Select(p => new { p.Kind, p.PriceNet, p.DepositNet })
            .ToListAsync(ct);

        if (prices.Count == 0)
        {
            return [];
        }

        var free = await db.ParkingSpaces
            .AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.State == UnitState.Available)
            .GroupBy(s => s.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return
        [
            .. from price in prices
               join available in free on price.Kind equals available.Kind
               orderby price.PriceNet descending
               select new ParkingOffer(price.Kind, price.PriceNet, price.DepositNet, available.Count)
        ];
    }

    /// <summary>The two name boxes as one line, which is what the emails and sales want.</summary>
    private string FullName => $"{Input.FirstName} {Input.Surname}".Trim();

    /// <summary>A country box left empty means Romania, so nothing is stored for it.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public bool CanSubmit => FailureKey is null;

    /// <summary>
    /// Whether the visitor can put this right on the same page.
    /// </summary>
    /// <remarks>
    /// True only for the parking failures, where nothing was held — not the apartment either —
    /// so the form is redrawn with fresh counts and they can ask for fewer spaces or none. The
    /// other failures mean the unit itself is gone, and offering the form again would be
    /// offering something that cannot be had.
    /// </remarks>
    public bool CanRetry { get; private set; }
}
