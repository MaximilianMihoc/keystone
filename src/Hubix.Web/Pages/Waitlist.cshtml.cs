using Hubix.Infrastructure.Sales;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hubix.Web.Pages;

/// <summary>
/// Somebody's own place in a queue, reached by the link in their confirmation email.
/// </summary>
/// <remarks>
/// The leave button is the point of this page as much as the place is. Emailing somebody to
/// say they are on a list, with no way off it but replying and hoping, is not consent — it is
/// a subscription.
/// </remarks>
public class WaitlistModel(WaitlistService waitlist) : PageModel
{
    public WaitlistPlace? Place { get; private set; }

    /// <summary>Set once they have taken themselves off, so the page says so.</summary>
    public bool JustLeft { get; private set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        Place = await waitlist.FindByTokenAsync(token, ct);

        return Place is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostLeaveAsync(string token, CancellationToken ct)
    {
        JustLeft = await waitlist.LeaveAsync(token, ct);

        Place = await waitlist.FindByTokenAsync(token, ct);

        return Place is null ? NotFound() : Page();
    }
}
