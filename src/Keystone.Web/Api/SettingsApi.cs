using System.Security.Claims;
using Keystone.Infrastructure.Email;
using Keystone.Infrastructure.Settings;
using Keystone.Web.Health;
using Keystone.Web.Notifications;
using Keystone.Web.Pages.Admin;
using Microsoft.Extensions.Options;

namespace Keystone.Web.Api;

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
            IOptionsSnapshot<KeystoneOptions> keystone,
            IOptions<ResendOptions> provider,
            CancellationToken ct) =>
        {
            var saved = await settings.CurrentAsync(ct);

            return Results.Ok(new
            {
                saved = saved is not null,
                savedAt = saved?.UpdatedAt,
                salesInbox = keystone.Value.SalesInbox,
                publicBaseUrl = keystone.Value.PublicBaseUrl,
                liveSendingEnabled = email.Value.LiveSendingEnabled,
                redirectAllTo = email.Value.RedirectAllTo,
                // Distinct because options binding appends a configured list to the property
                // default rather than replacing it. The shipped files no longer set either
                // list for that reason, but a deployment is free to, and a screen showing
                // *@example.com twice invites somebody to delete one of them.
                allowedRecipients = email.Value.AllowedRecipients.Distinct(StringComparer.OrdinalIgnoreCase),
                blockedRecipients = email.Value.BlockedRecipients.Distinct(StringComparer.OrdinalIgnoreCase),

                // Whether the switch below can do anything at all. An operator turning live
                // sending on deserves to know up front that nothing is wired up behind it —
                // and, once something is, not to be told otherwise.
                sendingPossible = EmailRegistration.CanSend(provider.Value, email.Value)
            });
        });

        admin.MapPut("/email", async (
            EmailSettingsRequest body,
            SettingsService settings,
            IOptionsSnapshot<EmailOptions> current,
            IHostEnvironment env,
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
                // Carried from configuration rather than the screen, which has no field for
                // it: the check below refuses live sending without a From, and validating a
                // blank one would make the switch impossible to turn on at all.
                From = current.Value.From,

                LiveSendingEnabled = body.LiveSendingEnabled,
                RedirectAllTo = body.RedirectAllTo,
                AllowedRecipients = [.. body.AllowedRecipients],
                BlockedRecipients = [.. body.BlockedRecipients]
            };

            // The same rules that stop a production instance starting. Refused here rather
            // than discovered at the next deploy — but only where they apply, exactly as
            // EnsureReady only applies them in Production. Development is meant to run on the
            // provider's sandbox sender, and refusing that leaves live sending impossible to
            // try at all. Outside Production they come back as warnings instead, so the screen
            // still says what a deploy would refuse.
            var refusals = ProductionReadiness.EmailProblems(proposed);

            if (refusals.Count > 0 && env.IsProduction())
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

            return Results.Ok(new { savedAt = saved.UpdatedAt, warnings = refusals });
        });

        // Proves the whole path, which is the only thing worth proving: the same guards, the
        // same composition, the same provider a reservation confirmation would take. A test
        // that took a shortcut past any of it would be a test of the shortcut.
        admin.MapPost("/email/test", async (
            TestSendRequest? body,
            SafeEmailSender sender,
            ClaimsPrincipal user,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            // Their own address unless they say otherwise: the common case is "does this work
            // at all", and nobody should have to type their own address to find out.
            var to = string.IsNullOrWhiteSpace(body?.To) ? AdminApi.ActorLabel(user) : body.To.Trim();

            var problem = ProjectApi.Length(to, "address", 320);

            if (problem is not null || !to.Contains('@'))
            {
                return Results.BadRequest(new { error = problem ?? $"\"{to}\" is not an email address." });
            }

            var message = new EmailMessage(
                to,
                "Keystone test message",
                $"""
                 This is a test message from Keystone, sent by {AdminApi.ActorLabel(user)}.

                 It went the same way every other message goes. If you are reading it in an
                 inbox, sending works. If it is in the outbox instead, the screen said so.
                 """,
                ReplyTo: null,

                // Both parts, so a provider or a client that prefers one over the other is
                // exercised here rather than discovered on a buyer's reservation.
                HtmlBody:
                $"""
                 <p>This is a test message from Keystone, sent by
                 {AdminApi.ActorLabel(user)}.</p>
                 <p>It went the same way every other message goes.</p>
                 """);

            try
            {
                var delivery = await sender.DeliverAsync(message, ct);

                return Results.Ok(new
                {
                    ok = true,
                    transmitted = delivery.Transmitted,
                    to = delivery.To,
                    reason = delivery.DivertedBecause
                });
            }
            catch (Exception ex)
            {
                // The provider refusing is the answer the button exists to give, so it is
                // reported rather than thrown: a 500 here would tell an operator nothing.
                loggers.CreateLogger(nameof(SettingsApi))
                    .LogError(ex, "A test message to {To} could not be sent", to);

                return Results.Ok(new { ok = false, to, error = ex.Message });
            }
        });
    }

    public sealed record TestSendRequest(string? To);

    public sealed record EmailSettingsRequest(
        string? SalesInbox,
        string? PublicBaseUrl,
        bool LiveSendingEnabled,
        string? RedirectAllTo,
        List<string> AllowedRecipients,
        List<string> BlockedRecipients);
}
