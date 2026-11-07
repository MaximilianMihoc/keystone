using System.Globalization;
using Keystone.Infrastructure.Auth;
using Keystone.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Keystone.Web.Notifications;

/// <summary>
/// Internal emails to staff. Always English — nobody on the team needs otherwise, and
/// a sign-in link is not the place to discover a missing translation.
/// </summary>
public sealed class StaffNotifier(IEmailSender email, IOptionsSnapshot<KeystoneOptions> options)
{
    /// <summary>
    /// Sends the sign-in link, and says what it sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The address is built here, from configuration, exactly as every other emailed link is.
    /// It used to be built by the page from <c>Request.Scheme</c> and the request's Host, which
    /// meant anybody who could post the login form could send a real member of staff a link
    /// pointing at their own server and collect a live token from it (issue #33). Nothing a
    /// visitor sends reaches this.
    /// </para>
    /// <para>
    /// The token is in the fragment — after the <c>#</c> — and not the query string, because a
    /// browser never sends a fragment to a server. A token in the path or the query is written
    /// to the request log, to every proxy's log in between, and to browser history, and it is a
    /// live credential for twelve hours of admin. This way the server only ever receives it in
    /// a form post. <c>verify.js</c> is the other half.
    /// </para>
    /// </remarks>
    public Task<string> SignInLinkAsync(LoginLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);

        var url = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/admin/login/verify#token={link.Token}";

        return SendAsync(link, url, ct);
    }

    private async Task<string> SendAsync(LoginLink link, string url, CancellationToken ct)
    {
        // The date is here because the link lives fifteen minutes: one issued at ten to midnight
        // expires tomorrow, and this used to say "today". Invariant, because the email is English
        // whatever language the admin happens to be reading the site in.
        var expires = Display.InRomania(link.ExpiresAt)
            .ToString("HH:mm 'on' dd MMM", CultureInfo.InvariantCulture);

        var body =
            $"""
             Hello {link.Name},

             Here is your sign-in link for the Keystone admin:

             {url}

             It works once and stops working at {expires} (Romania time).

             If you did not ask for this, ignore it — the link is useless without this email,
             and asking for a new one cancels this one.
             """;

        await email.SendAsync(
            new EmailMessage(link.Email, "Your Keystone sign-in link", body), ct);

        return url;
    }
}
