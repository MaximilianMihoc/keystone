using Hubix.Domain.Entities;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hubix.Web.Pages;

/// <summary>
/// Records an explicit language choice.
/// </summary>
/// <remarks>
/// Writes the culture cookie, which the cookie provider reads ahead of the Accept-Language
/// header. Browser detection stays the default; this exists so a visitor whose browser is set
/// to one language can still read the site in the other.
/// </remarks>
public class LanguageModel : PageModel
{
    public IActionResult OnGet(string? culture, string? returnUrl)
    {
        // Only cultures we actually serve. Anything else is ignored rather than trusted,
        // since this value arrives from the query string.
        if (Cultures.IsSupported(culture))
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture!)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true, // a language preference is not tracking
                    HttpOnly = false,   // readable by client script for a no-reload switcher
                    SameSite = SameSiteMode.Lax,
                    Secure = Request.IsHttps
                });
        }

        // Local URLs only: an open redirect here would be a phishing vector.
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
