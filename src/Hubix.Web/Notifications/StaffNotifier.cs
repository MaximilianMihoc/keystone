using Hubix.Infrastructure.Auth;
using Hubix.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Hubix.Web.Notifications;

/// <summary>
/// Internal emails to Hubix staff. Always English — nobody on the team needs otherwise, and
/// a sign-in link is not the place to discover a missing translation.
/// </summary>
public sealed class StaffNotifier(IEmailSender email, IOptionsSnapshot<HubixOptions> options)
{
    /// <summary>
    /// Sends the sign-in link, and says what it sent.
    /// </summary>
    /// <remarks>
    /// The address is built here, from configuration, exactly as every other emailed link is.
    /// It used to be built by the page from <c>Request.Scheme</c> and the request's Host, which
    /// meant anybody who could post the login form could send a real member of staff a link
    /// pointing at their own server and collect a live token from it (issue #33). Nothing a
    /// visitor sends reaches this.
    /// </remarks>
    public Task<string> SignInLinkAsync(LoginLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);

        var url = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/admin/login/verify?token={link.Token}";

        return SendAsync(link, url, ct);
    }

    private async Task<string> SendAsync(LoginLink link, string url, CancellationToken ct)
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

        await email.SendAsync(
            new EmailMessage(link.Email, "Your Hubix sign-in link", body), ct);

        return url;
    }
}
