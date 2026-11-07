using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Sales;
using Keystone.Web.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Web.Pages;

/// <summary>
/// Writing to Hubix, and asking for your data.
/// </summary>
/// <remarks>
/// One form for both, because that is how the second one arrives — nobody hunts for a separate
/// privacy page, they write "delete my details" into whatever box is in front of them. Picking
/// it from a list instead means the request is logged the moment it is made, with the deadline
/// running from then, rather than whenever somebody reads the inbox.
/// </remarks>
[EnableRateLimiting("reserve")]
public class ContactModel(
    KeystoneDbContext db,
    EnquiryService enquiries,
    EnquiryNotifier notifier,
    ILogger<ContactModel> logger) : PageModel
{
    public IReadOnlyList<ProjectChoice> Projects { get; private set; } = [];

    /// <summary>Which message was just sent, when arriving back from a successful post.</summary>
    public string? Sent { get; private set; }

    public bool Failed { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        /// <summary>"General", or the name of a <see cref="PrivacyRequestKind"/>.</summary>
        public string Kind { get; set; } = "General";

        /// <summary>Slug rather than id: it is in the query string when arriving from a project.</summary>
        public string? Project { get; set; }

        [Required(ErrorMessage = "Validation.FirstNameRequired")]
        [StringLength(100)]
        public string? FirstName { get; set; }

        [StringLength(100)]
        public string? Surname { get; set; }

        [Required(ErrorMessage = "Validation.EmailRequired")]
        [EmailAddress(ErrorMessage = "Validation.EmailInvalid")]
        [StringLength(320)]
        public string? Email { get; set; }

        [Phone(ErrorMessage = "Validation.PhoneInvalid")]
        [StringLength(50)]
        public string? Phone { get; set; }

        [Required(ErrorMessage = "Validation.MessageRequired")]
        [StringLength(4000)]
        public string? Message { get; set; }
    }

    public async Task OnGetAsync(string? project, string? sent, CancellationToken ct)
    {
        await LoadAsync(ct);

        Sent = sent is "General" or "Access" or "Erasure" ? sent : null;
        Input.Project = Projects.Any(p => p.Slug == project) ? project : null;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var kind = Enum.TryParse<PrivacyRequestKind>(Input.Kind, out var parsed)
            ? parsed
            : (PrivacyRequestKind?)null;

        var project = Projects.FirstOrDefault(p => p.Slug == Input.Project);

        try
        {
            var receipt = await enquiries.SubmitAsync(
                new ProspectDetails(
                    Email: Input.Email!,
                    FirstName: Input.FirstName,
                    Surname: Input.Surname,
                    Phone: Input.Phone,
                    City: null,
                    Country: null,
                    Locale: CultureInfo.CurrentUICulture.Name,
                    // The form asks nothing and promises nothing beyond answering. Consent to
                    // be marketed at is a different question, asked on the forms that need it.
                    ConsentGiven: false,
                    ConsentSource: "enquiry-form"),
                project?.Id,
                Input.Message!,
                kind,
                ct);

            await NotifyAsync(receipt, project?.Id, project?.Name, ct);

            return RedirectToPage(new { sent = kind?.ToString() ?? "General" });
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "An enquiry could not be saved");
            Failed = true;
            return Page();
        }
    }

    /// <summary>
    /// The message is already saved by this point. A confirmation that fails to send must not
    /// lose it, and must not show an error for something that worked.
    /// </summary>
    private async Task NotifyAsync(
        EnquiryReceipt receipt, Guid? projectId, string? projectName, CancellationToken ct)
    {
        try
        {
            await notifier.AcknowledgeAsync(Input.Email!, projectId, receipt.Request, ct);

            await notifier.SalesNotifiedAsync(
                projectId,
                Input.Email!,
                string.Join(' ', new[] { Input.FirstName, Input.Surname }
                    .Where(part => !string.IsNullOrWhiteSpace(part))),
                Input.Phone,
                projectName,
                Input.Message!,
                receipt.Request,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An enquiry was saved but its emails failed");
        }
    }

    private async Task LoadAsync(CancellationToken ct) =>
        Projects = await db.Projects
            .AsNoTracking()
            .Where(p => p.PublishedAt != null)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Select(p => new ProjectChoice(p.Id, p.Slug, p.Name))
            .ToListAsync(ct);

    public sealed record ProjectChoice(Guid Id, string Slug, string Name);
}
