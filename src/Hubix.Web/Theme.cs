using Microsoft.AspNetCore.Http;

namespace Hubix.Web;

/// <summary>
/// The visitor's colour scheme, remembered the way their language is.
/// </summary>
/// <remarks>
/// Both palettes already existed; only the browser could choose between them. This records an
/// explicit choice in a cookie, which the server reads and stamps on <c>&lt;html&gt;</c> before
/// anything paints — so there is no flash of the wrong theme, and the public site needs no
/// JavaScript to switch. No cookie means no attribute, which leaves
/// <c>prefers-color-scheme</c> in charge.
/// </remarks>
public static class Theme
{
    public const string CookieName = "hubix.theme";

    public const string Light = "light";

    public const string Dark = "dark";

    /// <summary>Not a stored value: choosing it clears the cookie.</summary>
    public const string System = "system";

    /// <summary>What the switcher offers, in the order it shows them.</summary>
    public static readonly string[] Offered = [Light, Dark, System];

    public static bool IsSupported(string? theme) => theme is Light or Dark;

    /// <summary>
    /// The value for the <c>data-theme</c> attribute, or null to follow the browser.
    /// </summary>
    public static string? FromRequest(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieName, out var theme) && IsSupported(theme)
            ? theme
            : null;
}
