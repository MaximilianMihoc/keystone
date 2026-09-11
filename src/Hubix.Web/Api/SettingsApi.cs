using System.Security.Claims;
using Hubix.Infrastructure.Email;
using Hubix.Infrastructure.Settings;
using Hubix.Web.Health;
using Hubix.Web.Notifications;
using Hubix.Web.Pages.Admin;
using Microsoft.Extensions.Options;

namespace Hubix.Web.Api;

/// <summary>
/// The email settings, as staff can change them.
/// </summary>
/// <remarks>
/// Admin only, and separately from editing, for the same reason erasure is: sales can change a
/// price, but deciding whether this system may send mail to real people is a different kind of
/// act. Saving is refused for any combination a production instance would refuse to start on,
/// because a setting that saves happily and then bricks the next deploy is the worse trap.
/// </remarks>
public static class SettingsApi
{
    public static void MapSettings(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/settings").RequireAuthorization(AuthPolicies.Admin);

        // What is actually in use, which is the file laid under whatever has been saved over
        // it. Showing the row alone would leave an operator reading blanks for live values.
        admin.MapGet("/email", async (
            SettingsService settings,
            IOptionsSnapshot<EmailOptions> email,
            IOptionsSnapshot<HubixOptions> hubix,
            CancellationToken ct) =>
        {
            var saved = await settings.CurrentAsync(ct);

            return Results.Ok(new
            {
                saved = saved is not null,
                savedAt = saved?.UpdatedAt,
                salesInbox = hubix.Value.SalesInbox,
                publicBaseUrl = hubix.Value.PublicBaseUrl,
                liveSendingEnabled = email.Value.LiveSendingEnabled,
                redirectAllTo = email.Value.RedirectAllTo,
                // Distinct because options binding appends configuration to the property
                // default rather than replacing it, so *@hubix.ro arrives here twice.
                allowedRecipients = email.Value.AllowedRecipients.Distinct(StringComparer.OrdinalIgnoreCase),
                blockedRecipients = email.Value.BlockedRecipients.Distinct(StringComparer.OrdinalIgnoreCase),

                // There is no provider registered, so nothing leaves the machine whatever this
                // says. An operator turning the switch on deserves to know that up front.
                sendingPossible = false
            });
        });

        admin.MapPut("/email", async (
            EmailSettingsRequest body,
            SettingsService settings,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var problem = ProjectApi.Length(body.SalesInbox, "sales inbox", 320)
                ?? ProjectApi.Length(body.RedirectAllTo, "redirect address", 320)
                ?? ProjectApi.Length(body.PublicBaseUrl, "public base URL", 500);

            if (problem is not null)
            {
                return Results.BadRequest(new { error = problem });
            }

            var proposed = new EmailOptions
            {
                LiveSendingEnabled = body.LiveSendingEnabled,
                RedirectAllTo = body.RedirectAllTo,
                AllowedRecipients = [.. body.AllowedRecipients],
                BlockedRecipients = [.. body.BlockedRecipients]
            };

            // The same rules that stop a production instance starting. Refused here rather
            // than discovered at the next deploy.
            var refusals = ProductionReadiness.EmailProblems(proposed);

            if (refusals.Count > 0)
            {
                return Results.BadRequest(new { error = string.Join(" ", refusals) });
            }

            var saved = await settings.SaveAsync(
                new EmailSettingsInput(
                    body.SalesInbox,
                    body.PublicBaseUrl,
                    body.LiveSendingEnabled,
                    body.RedirectAllTo,
                    body.AllowedRecipients,
                    body.BlockedRecipients),
                AdminApi.ActorId(user),
                AdminApi.ActorLabel(user),
                ct);

            return Results.Ok(new { savedAt = saved.UpdatedAt });
        });
    }

    public sealed record EmailSettingsRequest(
        string? SalesInbox,
        string? PublicBaseUrl,
        bool LiveSendingEnabled,
        string? RedirectAllTo,
        List<string> AllowedRecipients,
        List<string> BlockedRecipients);
}
