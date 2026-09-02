using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hubix.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>
    /// Whether mail may actually leave the machine. Off unless deliberately turned on.
    /// </summary>
    /// <remarks>
    /// Opt-in rather than opt-out. Forgetting to enable sending is a nuisance you notice
    /// immediately; forgetting to disable it emails real people, and you find out from them.
    /// </remarks>
    public bool LiveSendingEnabled { get; set; }

    /// <summary>
    /// When set, every message is re-addressed here regardless of who it was for.
    /// </summary>
    /// <remarks>
    /// The intended recipient is kept in the subject and at the top of the body, so a test
    /// inbox shows exactly what each person would have received and who they were. This is
    /// the only safe way to look at real mail while the data is real and the audience is not.
    /// Leave empty in production.
    /// </remarks>
    public string? RedirectAllTo { get; set; }

    /// <summary>
    /// Recipients allowed through when live sending is on. Wildcards allowed
    /// (<c>*@hubix.ro</c>). An empty list means everyone, which is what production wants.
    /// </summary>
    public List<string> AllowedRecipients { get; set; } = [];

    /// <summary>
    /// Addresses never contacted, whatever else is configured.
    /// </summary>
    /// <remarks>
    /// Hubix has not been approached about this system. Until they have, their real inboxes
    /// stay on this list, so a misconfiguration cannot introduce the project to them by
    /// sending them an automated reservation notice.
    /// </remarks>
    public List<string> BlockedRecipients { get; set; } = ["*@hubix.ro"];
}

/// <summary>
/// Decides whether a message may really be sent, and diverts it to the outbox if not.
/// </summary>
/// <remarks>
/// Every path out of this application goes through here. A message is transmitted only when
/// live sending is explicitly enabled, the recipient is not blocked, and any allowlist admits
/// them. Everything else is written to the local outbox exactly as it would have been sent,
/// so nothing is lost and nothing is silently discarded.
/// </remarks>
public sealed class SafeEmailSender(
    IEmailSender outbox,
    IOptions<EmailOptions> options,
    ILogger<SafeEmailSender> logger,
    IEmailSender? live = null) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        // Redirect first, so every later check sees the address that would really be used.
        var outgoing = Redirect(message);

        var verdict = Check(outgoing.To);

        if (verdict is not null)
        {
            logger.LogWarning(
                "Not sending to {To}: {Reason}. Written to the outbox instead.",
                outgoing.To, verdict);

            await outbox.SendAsync(outgoing, ct);
            return;
        }

        if (live is null)
        {
            logger.LogWarning(
                "Live sending is enabled but no provider is registered. " +
                "Message to {To} written to the outbox.", outgoing.To);

            await outbox.SendAsync(outgoing, ct);
            return;
        }

        await live.SendAsync(outgoing, ct);
    }

    /// <summary>
    /// Re-addresses a message to the test inbox, keeping the intended recipient visible.
    /// </summary>
    private EmailMessage Redirect(EmailMessage message)
    {
        if (string.IsNullOrWhiteSpace(_options.RedirectAllTo)
            || string.Equals(_options.RedirectAllTo, message.To, StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        logger.LogInformation(
            "Redirecting mail intended for {Original} to {Redirect}",
            message.To, _options.RedirectAllTo);

        return message with
        {
            To = _options.RedirectAllTo,
            Subject = $"[to: {message.To}] {message.Subject}",
            Body =
                $"""
                 — — — — — — — — — — — — — — — — — — — — — — — — — — — —
                 This is a test copy. It would have been sent to {message.To}.
                 — — — — — — — — — — — — — — — — — — — — — — — — — — — —

                 {message.Body}
                 """
        };
    }

    /// <summary>The reason this address may not be contacted, or null if it may.</summary>
    private string? Check(string recipient)
    {
        if (!_options.LiveSendingEnabled)
        {
            return "live sending is disabled";
        }

        if (_options.BlockedRecipients.Any(pattern => Matches(recipient, pattern)))
        {
            return "recipient is on the blocked list";
        }

        if (_options.AllowedRecipients.Count > 0
            && !_options.AllowedRecipients.Any(pattern => Matches(recipient, pattern)))
        {
            return "recipient is not on the allowed list";
        }

        return null;
    }

    internal static bool Matches(string address, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";

        return Regex.IsMatch(address, regex, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    }
}
