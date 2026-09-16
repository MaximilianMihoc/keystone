using System.Globalization;
using Hubix.Domain;
using Hubix.Infrastructure.Sales;
using Hubix.Web.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

namespace Hubix.Web.Pages;

/// <summary>
/// The other end of the link: where a reservation actually becomes a hold.
/// </summary>
/// <remarks>
/// Following the link only shows this page; a button on it does the holding. A link that held
/// an apartment by being fetched would be held by whatever scans the mailbox first, and taking
/// a real apartment off sale is too much to hand to a link preview.
/// </remarks>
[EnableRateLimiting("reserve")]
public class ReserveConfirmModel(
    HoldService holds,
    HoldNotifier notifier,
    IStringLocalizer<SharedResource> localizer,
    ILogger<ReserveConfirmModel> logger) : PageModel
{
    public HoldRequestPreview? Request { get; private set; }

    /// <summary>Set when there is nothing left to confirm, so the page can explain.</summary>
    public string? FailureKey { get; private set; }

    public object[] FailureArgs { get; private set; } = [];

    /// <summary>The apartment this was about, for the way back to its project.</summary>
    public string? ProjectSlug { get; private set; }

    public async Task OnGetAsync(string token, CancellationToken ct)
    {
        Request = await holds.FindRequestAsync(token, ct);
        ProjectSlug = Request?.ProjectSlug;

        if (Request is null)
        {
            FailureKey = "Reserve.LinkNotUsable";
        }
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken ct)
    {
        // Read before confirming, because confirming spends the link and the page still has
        // to be able to name the apartment if the hold itself then fails.
        Request = await holds.FindRequestAsync(token, ct);
        ProjectSlug = Request?.ProjectSlug;

        var outcome = await holds.ConfirmAsync(
            token, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);

        switch (outcome)
        {
            case HoldOutcome.Created created:
                await SendConfirmationsAsync(created, ct);
                return RedirectToPage("/Hold", new { token = created.AccessToken });

            case HoldOutcome.LinkNotUsable:
                FailureKey = "Reserve.LinkNotUsable";
                return Page();

            case HoldOutcome.ProspectBlocked:
                FailureKey = "Reserve.Blocked";
                return Page();

            case HoldOutcome.TooManyHolds tooMany:
                FailureKey = "Reserve.TooMany";
                FailureArgs = [tooMany.Limit];
                return Page();

            case HoldOutcome.NotEnoughParking shortfall:
                FailureKey = "Reserve.NotEnoughParkingNow";
                FailureArgs =
                [
                    shortfall.Available,
                    localizer[Display.ParkingSpaceKey(shortfall.Kind)].Value,
                    shortfall.Requested
                ];
                return Page();

            default:
                // Including the apartment having gone while the email sat unread, which is the
                // ordinary cost of not taking it off sale until somebody proves the address.
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
        var request = Request;

        if (request is null)
        {
            return;
        }

        var fullName = string.Join(" ", new[] { request.FirstName, request.Surname }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        try
        {
            // In the language the form was filled in, which is the buyer's, not whatever the
            // browser that opened the link happens to be set to.
            await notifier.HoldConfirmedAsync(
                request.Email, request.Locale, request.ProjectId, request.ProjectName,
                request.UnitCode, created.ExpiresAt, request.HoldDurationHours,
                created.AccessToken, ct);

            await notifier.SalesNotifiedAsync(
                request.ProjectId, request.ProjectName, request.UnitCode, request.Email,
                fullName.Length == 0 ? null : fullName, request.Phone,
                created.ExpiresAt, created.ParkingSpaces, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Hold {HoldId} was created but its confirmation email failed", created.HoldId);
        }
    }
}
