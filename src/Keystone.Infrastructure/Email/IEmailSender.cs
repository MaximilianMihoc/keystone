using Microsoft.Extensions.Logging;

namespace Keystone.Infrastructure.Email;

/// <summary>
/// One message, in the words of this application rather than any provider's.
/// </summary>
/// <param name="Body">
/// The plain-text body. Always present: every template here is plain text, and a message with
/// no text part is the kind spam filters dislike.
/// </param>
/// <param name="HtmlBody">
/// The same message as HTML, where a template has one. Sent alongside the text rather than
/// instead of it, so a reader gets whichever their client prefers.
/// </param>
public sealed record EmailMessage(
    string To,
    string Subject,
    string Body,
    string? ReplyTo = null,
    string? HtmlBody = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>
/// Development sender: writes the message to a file, notes that it did, and sends nothing.
/// </summary>
/// <remarks>
/// Deliberately not a no-op. A silently discarded email is indistinguishable from a broken one,
/// and reservation confirmations are the sort of thing that has to be seen to be trusted.
/// Replace with a real provider before any of this reaches a buyer.
/// </remarks>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger, string outboxPath)
    : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Directory.CreateDirectory(outboxPath);

        var file = Path.Combine(
            outboxPath,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Sanitise(message.To)}.txt");

        await File.WriteAllTextAsync(
            file,
            $"""
             To:      {message.To}
             Subject: {message.Subject}
             ReplyTo: {message.ReplyTo ?? "-"}
             Sent:    {DateTimeOffset.UtcNow:u}

             {message.Body}
             """,
            ct);

        // Recipient, subject and where it landed. Never the body: every bearer token this
        // portal issues travels in one, and a log is kept longer, copied further and read by
        // more people than a mailbox (issue #53). The file just written is where to read it.
        logger.LogInformation(
            "EMAIL to {To} written to the outbox as {File}. Subject: {Subject}",
            message.To, Path.GetFileName(file), message.Subject);
    }

    private static string Sanitise(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
}
