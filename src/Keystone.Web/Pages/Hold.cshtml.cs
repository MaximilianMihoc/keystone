using System.Globalization;
using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Keystone.Web.Pages;

/// <summary>
/// A prospect's view of their own hold, reached by the secret link in their email.
/// </summary>
public class HoldModel(HoldService holds, TimeProvider clock) : PageModel
{
    public Hold Hold { get; private set; } = null!;

    public string ProjectName { get; private set; } = "";

    public string ProjectSlug { get; private set; } = "";

    public string UnitCode { get; private set; } = "";

    public string TypeDisplay { get; private set; } = "";

    public bool IsActive { get; private set; }

    public bool JustCancelled { get; private set; }

    /// <summary>The parking spaces held alongside the apartment, if any.</summary>
    public IReadOnlyList<HeldSpace> ParkingSpaces { get; private set; } = [];

    public sealed record HeldSpace(string Code, ParkingSpaceKind Kind, int Level);

    /// <summary>Seconds left, for the countdown. Zero once it has run out.</summary>
    public int SecondsRemaining { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        var loaded = await LoadAsync(token, ct);
        return loaded ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostCancelAsync(string token, CancellationToken ct)
    {
        if (!await LoadAsync(token, ct))
        {
            return NotFound();
        }

        if (IsActive)
        {
            // The operator's own rule: a prospect who abandons a hold may not take another, "to avoid
            // reservations cancelled in series by the same person". Cancelling counts as a
            // lapse for exactly that reason, and the page says so before they click.
            await holds.ReleaseAsync(Hold.Id, HoldReleaseReason.Cancelled, null, ct);

            return RedirectToPage("/Hold", new { token, cancelled = true });
        }

        return RedirectToPage("/Hold", new { token });
    }

    private async Task<bool> LoadAsync(string token, CancellationToken ct)
    {
        var hold = await holds.FindByTokenAsync(token, ct);

        if (hold?.Unit?.Project is null)
        {
            return false;
        }

        Hold = hold;
        ProjectName = hold.Unit.Project.Name;
        ProjectSlug = hold.Unit.Project.Slug;
        UnitCode = hold.Unit.Code;
        TypeDisplay = hold.Unit.UnitType?.DisplayName(CultureInfo.CurrentUICulture.Name) ?? "";

        // Which space is theirs. The codes are the whole point of numbering them.
        ParkingSpaces =
        [
            .. hold.ParkingSpaces
                .OrderBy(s => s.Code, StringComparer.Ordinal)
                .Select(s => new HeldSpace(s.Code, s.Kind, s.Level))
        ];

        var now = clock.GetUtcNow();

        // A hold past its expiry is over even if the job has not swept it yet, so the page
        // never shows a countdown that has already run out.
        IsActive = hold.IsActive && hold.ExpiresAt > now;
        SecondsRemaining = (int)Math.Max(0, (hold.ExpiresAt - now).TotalSeconds);
        JustCancelled = Request.Query.ContainsKey("cancelled");

        return true;
    }
}
