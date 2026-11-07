using System.Text.Json;
using Keystone.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keystone.Infrastructure.Sales;

/// <summary>What was written down, and the clock it started if it started one.</summary>
public sealed record EnquiryReceipt(Guid EnquiryId, PrivacyRequest? Request);

/// <summary>
/// The contact form. Replaces the operator's "trimite intrebari".
/// </summary>
/// <remarks>
/// It is also the way in for the two data rights, because that is how they actually arrive —
/// somebody writes "delete my details" into whatever form is in front of them. Logging that as
/// a request here rather than waiting for staff to notice an email is the difference between a
/// deadline that runs from when they asked and one that runs from when somebody got round to
/// it, and Article 12(3) counts the first.
/// </remarks>
public sealed class EnquiryService(
    KeystoneDbContext db,
    TimeProvider clock,
    ILogger<EnquiryService> logger)
{
    /// <param name="privacyKind">
    /// Null for an ordinary question. Set when the message is somebody exercising a data right.
    /// </param>
    public async Task<EnquiryReceipt> SubmitAsync(
        ProspectDetails person,
        Guid? projectId,
        string message,
        PrivacyRequestKind? privacyKind,
        CancellationToken ct)
    {
        var email = person.Email.Trim();
        Prospect? prospect;

        // A question is a reason to write somebody down. "Delete me" is not: creating a
        // record for a stranger in order to forget them would be the request's own opposite.
        // So a data right from an address we do not know leaves the enquiry unattached, and
        // the request itself carries the address.
        if (privacyKind is null)
        {
            bool created;
            (prospect, created) = await ProspectRegistry.GetOrCreateAsync(
                db, email, "enquiry", person.Locale, ct);

            if (created)
            {
                person.ApplyTo(prospect);
            }
            else
            {
                // Nobody has shown this address is theirs, and the record found under it may
                // be a buyer's. Missing details are worth having; their name, number and the
                // language we write to them in are not this form's to change (issue #30).
                person.FillBlanksOf(prospect);
            }
        }
        else
        {
            prospect = await db.Prospects.FirstOrDefaultAsync(p => p.Email == email, ct);
        }

        var enquiry = new Enquiry
        {
            ProjectId = projectId,
            ProspectId = prospect?.Id,
            Message = message.Trim()
        };

        db.Enquiries.Add(enquiry);

        PrivacyRequest? request = null;

        if (privacyKind is { } kind)
        {
            request = new PrivacyRequest
            {
                Email = email,
                Kind = kind,
                RequestedAt = clock.GetUtcNow(),
                Note = "Asked through the contact form."
            };

            db.PrivacyRequests.Add(request);

            db.AuditLogs.Add(new AuditLog
            {
                // The person themselves, not staff. Erasure redacts audit rows by this label,
                // so the address goes the same way as every other trace of them.
                ActorType = "prospect",
                ActorId = prospect?.Id,
                ActorLabel = email,
                EntityType = "privacy-request",
                EntityId = request.Id,
                Action = "privacy.request.logged",
                After = JsonSerializer.Serialize(new
                {
                    Kind = kind.ToString(),
                    request.RequestedAt,
                    request.DueAt,
                    Source = "contact-form"
                })
            });
        }

        // One write. An enquiry that saved without the request it is asking for would leave a
        // deadline running that nothing in the system knows about.
        await db.SaveChangesAsync(ct);

        if (request is null)
        {
            logger.LogInformation("An enquiry arrived about project {ProjectId}", projectId);
        }
        else
        {
            logger.LogInformation(
                "A {Kind} request arrived through the contact form, due {Due:u}",
                request.Kind, request.DueAt);
        }

        return new EnquiryReceipt(enquiry.Id, request);
    }
}
