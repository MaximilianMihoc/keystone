using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Keystone.Web.Pages;

/// <summary>
/// Both kinds of dead end: a page that is not there, and a request that went wrong.
/// </summary>
/// <remarks>
/// <para>
/// Reached by re-execution rather than redirection, so the response keeps the status code it
/// arrived with. A search engine told 200 for a page that does not exist would keep the dead
/// link, and a monitor would never see a failure.
/// </para>
/// <para>
/// Properties rather than an <c>OnGet</c>, because re-execution keeps the method of the
/// request that failed. A form posted to an address that is not there re-enters this page as
/// a POST, no handler matches, and a page that worked out what to say inside <c>OnGet</c>
/// would apologise for a fault when all that happened was a wrong address.
/// </para>
/// </remarks>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    /// <summary>A wrong address rather than a fault. Different words, and nobody's fault.</summary>
    public bool Missing => Response.StatusCode == StatusCodes.Status404NotFound;

    /// <summary>
    /// Only worth showing when there is something in the logs to look it up by. A mistyped
    /// URL leaves no entry, and a reference to nothing is noise on the page.
    /// </summary>
    public string? RequestId =>
        Missing ? null : Activity.Current?.Id ?? HttpContext.TraceIdentifier;

    public bool ShowRequestId => RequestId is not null;
}
