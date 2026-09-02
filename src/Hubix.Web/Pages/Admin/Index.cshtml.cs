using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hubix.Web.Pages.Admin;

/// <summary>
/// Hosts the React admin client.
/// </summary>
/// <remarks>
/// The route is a catch-all so the client can own its own paths: a deep link or a refresh on
/// /admin/projects/x/units must still reach this page rather than 404.
/// </remarks>
[Authorize(Policy = AuthPolicies.Staff)]
public class IndexModel(IWebHostEnvironment environment) : PageModel
{
    /// <summary>
    /// Whether the built client is actually on disk, so a missing build explains itself
    /// instead of rendering a blank page.
    /// </summary>
    public bool BundleExists { get; private set; }

    public void OnGet()
    {
        // Fully qualified: PageModel already has a File() method.
        BundleExists = System.IO.File.Exists(
            Path.Combine(environment.WebRootPath, "admin-assets", "index.js"));
    }
}

public static class AuthPolicies
{
    /// <summary>Any active staff account.</summary>
    public const string Staff = "staff";

    /// <summary>Roles allowed to change data, as opposed to reading it.</summary>
    public const string CanEdit = "can-edit";
}
