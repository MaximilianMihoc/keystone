using Keystone.Infrastructure.Email;
using Shouldly;

namespace Keystone.Tests;

/// <summary>
/// Turning one of our messages into one of Resend's.
/// </summary>
/// <remarks>
/// Composition only, and no network: whether a message may be sent at all is
/// <see cref="SafeEmailSender"/>'s decision and is tested there. This is about the mapping,
/// which is where the silent failures live — a dropped reply-to looks like nothing at all
/// until a buyer replies and nobody is listening.
/// </remarks>
public class ResendEmailSenderTests
{
    private static EmailOptions From(string? name = null) =>
        new() { From = "portal@example.test", FromName = name };

    [Fact]
    public void The_sender_is_the_configured_address()
    {
        var message = ResendEmailSender.Compose(
            new EmailMessage("buyer@example.test", "Subject", "Body"), From());

        message.From!.Email.ShouldBe("portal@example.test");
        message.To.Single().Email.ShouldBe("buyer@example.test");
        message.Subject.ShouldBe("Subject");
    }

    [Fact]
    public void A_display_name_is_used_when_there_is_one()
    {
        var message = ResendEmailSender.Compose(
            new EmailMessage("buyer@example.test", "Subject", "Body"), From("Hubix Portal"));

        message.From!.DisplayName.ShouldBe("Hubix Portal");
    }

    [Fact]
    public void A_blank_display_name_is_left_off_rather_than_sent_empty()
    {
        var message = ResendEmailSender.Compose(
            new EmailMessage("buyer@example.test", "Subject", "Body"), From("   "));

        message.From!.DisplayName.ShouldBeNull();
    }

    /// <summary>
    /// The header that makes a buyer's reply reach sales rather than an unread mailbox.
    /// </summary>
    [Fact]
    public void A_reply_goes_to_the_sales_inbox_that_was_asked_for()
    {
        var message = ResendEmailSender.Compose(
            new EmailMessage("buyer@example.test", "Subject", "Body", "sales@example.test"),
            From());

        message.ReplyTo!.Single().Email.ShouldBe("sales@example.test");
    }

    [Fact]
    public void No_reply_to_leaves_the_header_off_rather_than_empty()
    {
        var message = ResendEmailSender.Compose(
            new EmailMessage("buyer@example.test", "Subject", "Body"), From());

        message.ReplyTo.ShouldBeNull();
    }

    /// <summary>
    /// Every template here is plain text today. The text part goes on every message regardless:
    /// it is what a plain-text client shows, and its absence is a thing spam filters count.
    /// </summary>
    [Fact]
    public void A_plain_text_message_is_sent_as_text_with_no_html_part()
    {
        const string body = "Line one.\n\nLine two with a link: https://example.test/hold/abc";

        var message = ResendEmailSender.Compose(
            new EmailMessage("buyer@example.test", "Subject", body), From());

        message.TextBody.ShouldBe(body);
        message.HtmlBody.ShouldBeNull();
    }

    [Fact]
    public void A_message_with_both_parts_carries_both()
    {
        var message = ResendEmailSender.Compose(
            new EmailMessage("buyer@example.test", "Subject", "Plain", HtmlBody: "<p>Rich</p>"),
            From());

        message.TextBody.ShouldBe("Plain");
        message.HtmlBody.ShouldBe("<p>Rich</p>");
    }
}
