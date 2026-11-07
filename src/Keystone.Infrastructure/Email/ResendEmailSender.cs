using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resend;
using ResendMessage = Resend.EmailMessage;

namespace Keystone.Infrastructure.Email;

/// <summary>
/// What Resend needs that nothing else does.
/// </summary>
/// <remarks>
/// The key only. The sender address and display name live on <see cref="EmailOptions"/> because
/// they are not Resend's business — they belong to the domain whose DNS records were published,
/// and they survive a change of provider unchanged.
/// </remarks>
public sealed class ResendOptions
{
    public const string Section = "Resend";

    /// <summary>
    /// Never in appsettings. User-secrets locally, the host's secret store in production.
    /// </summary>
    public string ApiKey { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Hands a message to Resend.
/// </summary>
/// <remarks>
/// <para>
/// The only file in the solution that knows Resend exists. Everything above it — the notifiers,
/// the pages, the jobs — depends on <see cref="IEmailSender"/> and would not notice this being
/// replaced by a Postmark or SES equivalent: a new class here, one line of registration, and
/// nothing else in the application changes.
/// </para>
/// <para>
/// Deliberately knows nothing about whether a message <em>should</em> be sent. That decision
/// belongs to <see cref="SafeEmailSender"/>, which wraps this one, and keeping the two apart is
/// what makes "every path out goes through the guards" a statement about the code rather than a
/// hope. Nothing should ever register this as <see cref="IEmailSender"/> directly.
/// </para>
/// </remarks>
public sealed class ResendEmailSender(
    IResend resend,
    IOptionsSnapshot<EmailOptions> email,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var outgoing = Compose(message, email.Value);

        try
        {
            var sent = await resend.EmailSendAsync(outgoing, ct);

            logger.LogInformation(
                "Resend accepted {Subject} for {To} as {MessageId}",
                message.Subject, message.To, sent.Content);
        }
        catch (ResendException ex)
        {
            // The recipient and subject, never the body: one is needed to work out which
            // message failed, the other is somebody's private correspondence. The key is not
            // here to be logged — it lives in the client, and the exception does not carry it.
            logger.LogError(ex,
                "Resend refused {Subject} for {To}: {ErrorType} (HTTP {Status}){Transient}",
                message.Subject, message.To, ex.ErrorType, ex.StatusCode,
                ex.IsTransient ? ", worth retrying" : "");

            throw;
        }
    }

    /// <summary>
    /// Turns one of our messages into one of Resend's.
    /// </summary>
    /// <remarks>
    /// Separated so it can be asserted on without an API key or a network. The reply-to is the
    /// part worth testing: it is what makes a buyer's reply reach the sales inbox rather than a
    /// noreply address, and losing it would look like nothing at all until somebody replied.
    /// </remarks>
    internal static ResendMessage Compose(EmailMessage message, EmailOptions options)
    {
        var outgoing = new ResendMessage
        {
            From = new EmailAddress
            {
                Email = options.From,
                DisplayName = string.IsNullOrWhiteSpace(options.FromName) ? null : options.FromName
            },
            Subject = message.Subject,

            // Both parts when there are two. A text part on every message is deliberate: it is
            // what a plain-text client shows, and its absence is a thing spam filters count.
            TextBody = message.Body,
            HtmlBody = message.HtmlBody
        };

        outgoing.To.Add(message.To);

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            outgoing.ReplyTo = message.ReplyTo;
        }

        return outgoing;
    }
}
