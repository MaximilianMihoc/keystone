using Hubix.Infrastructure.Auth;
using Hubix.Infrastructure.Email;

namespace Hubix.Web.Notifications;

/// <summary>
/// Internal emails to Hubix staff. Always English — nobody on the team needs otherwise, and
/// a sign-in link is not the place to discover a missing translation.
/// </summary>
public sealed class StaffNotifier(IEmailSender email)
{
    public Task SignInLinkAsync(LoginLink link, string url, CancellationToken ct)
    {
        var body =
            $"""
             Hello {link.Name},

             Here is your sign-in link for the Hubix admin:

             {url}

             It works once and stops working at {link.ExpiresAt.ToLocalTime():HH:mm} today.

             If you did not ask for this, ignore it — the link is useless without this email,
             and asking for a new one cancels this one.
             """;

        return email.SendAsync(
            new EmailMessage(link.Email, "Your Hubix sign-in link", body), ct);
    }
}
