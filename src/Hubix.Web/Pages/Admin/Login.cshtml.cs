using System.ComponentModel.DataAnnotations;
using Hubix.Infrastructure.Auth;
using Hubix.Web.Auth;
using Hubix.Web.Notifications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace Hubix.Web.Pages.Admin;

[EnableRateLimiting("login")]
public class LoginModel(
    StaffAuthService auth,
    StaffNotifier notifier,
    IWebHostEnvironment environment,
    ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter your Hubix email address.")]
    [EmailAddress(ErrorMessage = "That does not look like an email address.")]
    public string? Email { get; set; }

    public bool LinkSent { get; private set; }

    /// <summary>
    /// In development the sign-in link is shown on the page, because there is no mail
    /// provider and digging through the outbox to log in is a poor way to spend a morning.
    /// Never populated outside Development.
    /// </summary>
    public string? DevelopmentLink { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Admin/Index");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var link = await auth.IssueLoginTokenAsync(
            Email!, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);

        // Always the same answer. Telling an unauthenticated visitor whether an address is
        // a Hubix account hands them a staff list.
        LinkSent = true;

        if (link is null)
        {
            return Page();
        }

        try
        {
            // The notifier builds the address from configuration. This page deliberately does
            // not: a link built from the request would point wherever the request's Host said,
            // which is not this page's to decide and not a visitor's either (issue #33).
            var url = await notifier.SignInLinkAsync(link, ct);

            if (environment.IsDevelopment())
            {
                DevelopmentLink = url;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send the sign-in link to {Email}", link.Email);
        }

        return Page();
    }
}

public class VerifyModel(StaffAuthService auth, ILogger<VerifyModel> logger) : PageModel
{
    /// <summary>
    /// Renders the page that hands the token back. There is nothing to read here.
    /// </summary>
    /// <remarks>
    /// The token is in the fragment, which the browser keeps to itself, so this request carries
    /// no credential at all — nothing for a request log, a proxy or browser history to keep.
    /// It also means a mail scanner or link prefetcher following the link cannot spend the
    /// token: it does not run <c>verify.js</c> and it does not post forms.
    /// </remarks>
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return RedirectToPage("/Admin/Login");
        }

        var staff = await auth.ConsumeAsync(token, ct);

        if (staff is null)
        {
            TempData["LoginError"] = "That sign-in link has expired or been used already. Ask for a new one.";
            return RedirectToPage("/Admin/Login");
        }

        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, staff.Id.ToString()),
            new(System.Security.Claims.ClaimTypes.Email, staff.Email),
            new(System.Security.Claims.ClaimTypes.Name, staff.Name),
            new(System.Security.Claims.ClaimTypes.Role, staff.Role.ToString())
        };

        var identity = new System.Security.Claims.ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);

        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
        };

        // When this session began and when its account was last looked at. The twelve hours
        // above slide; the ceiling those two carry does not (issue #34).
        StaffSessionValidator.Begin(properties, DateTimeOffset.UtcNow);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new System.Security.Claims.ClaimsPrincipal(identity),
            properties);

        logger.LogInformation("{Email} signed in as {Role}", staff.Email, staff.Role);

        return RedirectToPage("/Admin/Index");
    }
}

/// <remarks>
/// No antiforgery token, because the admin client's sign-out is a plain form with no way to
/// get one, and requiring it refused every sign-out with a 400 (issue #67). That is safe here:
/// the worst a forged request can do is sign somebody out, and the SameSite=Lax staff cookie
/// is not sent on a cross-site POST anyway. POST only, so a prefetch or a link scanner cannot
/// end a session.
/// </remarks>
[IgnoreAntiforgeryToken]
public class LogoutModel : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Admin/Login");
    }
}
