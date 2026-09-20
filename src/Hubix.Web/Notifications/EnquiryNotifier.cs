using System.Globalization;
using Hubix.Domain.Entities;
using Hubix.Infrastructure.Email;
using Microsoft.Extensions.Localization;

namespace Hubix.Web.Notifications;

/// <summary>
/// The two emails a contact form message produces: one back to whoever wrote it, one to sales.
/// </summary>
/// <remarks>
/// No culture juggling here, unlike <see cref="HoldNotifier"/>. Both of these are sent while
/// the visitor's own request is still on the stack, so the current culture is already theirs.
/// </remarks>
public sealed class EnquiryNotifier(
    IEmailSender email,
    IStringLocalizer<SharedResource> localizer,
    SalesInboxResolver inboxes)
{
    /// <summary>
    /// Tells them we have it. For a data right it also tells them the date it is due, which is
    /// the only thing that makes the deadline theirs to hold us to.
    /// </summary>
    public async Task AcknowledgeAsync(
        string toEmail, Guid? projectId, PrivacyRequest? request, CancellationToken ct)
    {
        var (subject, body) = request is null
            ? (localizer["Email.EnquirySubject"].Value,
               localizer["Email.EnquiryBody"].Value)
            : (localizer["Email.PrivacyRequestSubject"].Value,
               string.Format(
                   CultureInfo.CurrentCulture,
                   localizer["Email.PrivacyRequestBody"].Value,
                   localizer["Contact.Kind" + request.Kind].Value,
                   Display.InRomania(request.DueAt)
                       .ToString("dd MMMM yyyy", CultureInfo.CurrentCulture)));

        await email.SendAsync(
            new EmailMessage(toEmail, subject, body, await inboxes.ForAsync(projectId, ct)), ct);
    }

    /// <summary>Tells sales. Always English — it is an internal message.</summary>
    public async Task SalesNotifiedAsync(
        Guid? projectId,
        string fromEmail,
        string? name,
        string? phone,
        string? projectName,
        string message,
        PrivacyRequest? request,
        CancellationToken ct)
    {
        var about = projectName is null ? "" : $"About: {projectName}\n";

        // A data right is not a lead, and putting the deadline in the subject line is the
        // cheapest way to stop it sitting in an inbox for five weeks.
        var deadline = request is null
            ? ""
            : $"""

               This is a GDPR {request.Kind} request. It is due by {Display.InRomania(request.DueAt):dd MMM yyyy}.
               Check who they are before acting on it, and answer it from the Prospects screen.

               """;

        var body =
            $"""
             {name ?? "(no name given)"} <{fromEmail}>{(phone is null ? "" : $"  {phone}")}
             {about}{deadline}
             {message}
             """;

        var subject = request is null
            ? $"Enquiry: {name ?? fromEmail}"
            : $"GDPR {request.Kind} request: {fromEmail}";

        await email.SendAsync(
            new EmailMessage(await inboxes.ForAsync(projectId, ct), subject, body, fromEmail), ct);
    }
}
