using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Keystone.Web.Pages;

/// <summary>
/// Records an explicit light or dark choice, or clears it.
/// </summary>
/// <remarks>
/// The same shape as <see cref="LanguageModel"/>, for the same reason: the public site runs no
/// JavaScript, so a preference is a link, a cookie and a redirect back to where you were.
/// </remarks>
public class ThemeModel : PageModel
{
    public IActionResult OnGet(string? theme, string? returnUrl)
    {
        if (Web.Theme.IsSupported(theme))
        {
            Response.Cookies.Append(Web.Theme.CookieName, theme!, CookieOptions());
        }
        else if (theme == Web.Theme.System)
        {
            // Following the browser is the absence of a choice, not a third value to store.
            Response.Cookies.Delete(Web.Theme.CookieName);
        }

        // Local URLs only: an open redirect here would be a phishing vector.
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    private CookieOptions CookieOptions() => new()
    {
        Expires = DateTimeOffset.UtcNow.AddYears(1),
        IsEssential = true, // a colour scheme preference is not tracking
        HttpOnly = false,   // the admin client reads and writes it without a reload
        SameSite = SameSiteMode.Lax,
        Secure = Request.IsHttps,
        Path = "/"
    };
}
