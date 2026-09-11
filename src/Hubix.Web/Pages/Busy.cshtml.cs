using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hubix.Web.Pages;

/// <summary>
/// Where the rate limiter sends somebody it has just turned away.
/// </summary>
/// <remarks>
/// The page answers 429 itself rather than 200. The visitor is being refused, and a crawler
/// or a script that follows the redirect should be told that in the only place it reads.
/// </remarks>
public class BusyModel : PageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status429TooManyRequests;
}
