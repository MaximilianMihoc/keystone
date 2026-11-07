using Keystone.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Keystone.Tests;

/// <summary>
/// The guard that stands between this project and a real company's inbox.
/// </summary>
public class SafeEmailSenderTests
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static (SafeEmailSender Sender, RecordingSender Outbox, RecordingSender Live)
        Build(Action<EmailOptions>? configure = null)
    {
        var options = new EmailOptions();
        configure?.Invoke(options);

        var outbox = new RecordingSender();
        var live = new RecordingSender();

        return (
            new SafeEmailSender(
                outbox,
                Options.Create(options),
                NullLogger<SafeEmailSender>.Instance,
                live),
            outbox,
            live);
    }

    private static EmailMessage To(string address) =>
        new(address, "Subject", "Body");

    [Fact]
    public async Task Sends_nothing_by_default()
    {
        // Live sending is opt-in. Forgetting to enable it is a nuisance you notice at once;
        // forgetting to disable it emails real people.
        var (sender, outbox, live) = Build();

        await sender.SendAsync(To("buyer@example.ro"), CancellationToken.None);

        live.Sent.ShouldBeEmpty();
        outbox.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Never_emails_Hubix_even_when_live_sending_is_on()
    {
        // Hubix has not been approached about this system. They must not find out about it
        // from an automated reservation notice.
        var (sender, outbox, live) = Build(o => o.LiveSendingEnabled = true);

        await sender.SendAsync(To("contact@hubix.ro"), CancellationToken.None);
        await sender.SendAsync(To("cristian@hubix.ro"), CancellationToken.None);

        live.Sent.ShouldBeEmpty();
        outbox.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Sends_to_anyone_else_once_live_sending_is_on()
    {
        var (sender, outbox, live) = Build(o => o.LiveSendingEnabled = true);

        await sender.SendAsync(To("buyer@example.ro"), CancellationToken.None);

        live.Sent.Count.ShouldBe(1);
        outbox.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_allowlist_narrows_delivery_to_just_those_addresses()
    {
        // How a staging environment is safely pointed at a real provider.
        var (sender, outbox, live) = Build(o =>
        {
            o.LiveSendingEnabled = true;
            o.AllowedRecipients = ["*@example.ro", "max.test@gmail.com"];
        });

        await sender.SendAsync(To("buyer@example.ro"), CancellationToken.None);
        await sender.SendAsync(To("max.test@gmail.com"), CancellationToken.None);
        await sender.SendAsync(To("someone@elsewhere.com"), CancellationToken.None);

        live.Sent.Select(m => m.To).ShouldBe(["buyer@example.ro", "max.test@gmail.com"]);
        outbox.Sent.Single().To.ShouldBe("someone@elsewhere.com");
    }

    [Fact]
    public async Task The_block_list_beats_the_allow_list()
    {
        var (sender, outbox, live) = Build(o =>
        {
            o.LiveSendingEnabled = true;
            o.AllowedRecipients = ["*@hubix.ro"]; // explicitly allowed...
        });

        await sender.SendAsync(To("contact@hubix.ro"), CancellationToken.None);

        // ...and still refused, because blocking is checked first.
        live.Sent.ShouldBeEmpty();
        outbox.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_blocked_message_is_kept_not_discarded()
    {
        var (sender, outbox, _) = Build(o => o.LiveSendingEnabled = true);
        var message = new EmailMessage("contact@hubix.ro", "Hold: Ap.104", "Body here");

        await sender.SendAsync(message, CancellationToken.None);

        // A silently dropped email is indistinguishable from a broken one.
        outbox.Sent.Single().ShouldBe(message);
    }

    [Fact]
    public async Task Falls_back_to_the_outbox_when_no_provider_is_registered()
    {
        var outbox = new RecordingSender();
        var sender = new SafeEmailSender(
            outbox,
            Options.Create(new EmailOptions { LiveSendingEnabled = true }),
            NullLogger<SafeEmailSender>.Instance,
            live: null);

        await sender.SendAsync(To("buyer@example.ro"), CancellationToken.None);

        outbox.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Redirect_sends_everything_to_the_test_inbox_instead()
    {
        var (sender, _, live) = Build(o =>
        {
            o.LiveSendingEnabled = true;
            o.RedirectAllTo = "tester@example.com";
        });

        await sender.SendAsync(To("buyer@example.ro"), CancellationToken.None);
        await sender.SendAsync(To("someone.else@example.ro"), CancellationToken.None);

        live.Sent.Select(m => m.To).ShouldAllBe(to => to == "tester@example.com");
        live.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Redirect_keeps_the_intended_recipient_visible()
    {
        var (sender, _, live) = Build(o =>
        {
            o.LiveSendingEnabled = true;
            o.RedirectAllTo = "tester@example.com";
        });

        await sender.SendAsync(
            new EmailMessage("buyer@example.ro", "Ap.104 is held for you", "Body"),
            CancellationToken.None);

        var sent = live.Sent.Single();

        // Otherwise a test inbox full of redirected mail is impossible to read.
        sent.Subject.ShouldBe("[to: buyer@example.ro] Ap.104 is held for you");
        sent.Body.ShouldContain("buyer@example.ro");
        sent.Body.ShouldContain("Body");
    }

    [Fact]
    public async Task Redirect_also_catches_mail_meant_for_Hubix()
    {
        // Belt and braces: even without the block list, nothing addressed to Hubix
        // reaches them while a redirect is set.
        var (sender, _, live) = Build(o =>
        {
            o.LiveSendingEnabled = true;
            o.RedirectAllTo = "tester@example.com";
            o.BlockedRecipients = [];
        });

        await sender.SendAsync(To("contact@hubix.ro"), CancellationToken.None);

        live.Sent.Single().To.ShouldBe("tester@example.com");
    }

    [Fact]
    public async Task Redirect_does_not_double_wrap_a_message_already_addressed_there()
    {
        var (sender, _, live) = Build(o =>
        {
            o.LiveSendingEnabled = true;
            o.RedirectAllTo = "tester@example.com";
        });

        await sender.SendAsync(
            new EmailMessage("tester@example.com", "Direct", "Body"), CancellationToken.None);

        live.Sent.Single().Subject.ShouldBe("Direct");
    }

    [Theory]
    [InlineData("contact@hubix.ro", "*@hubix.ro", true)]
    [InlineData("CONTACT@HUBIX.RO", "*@hubix.ro", true)]   // case-insensitive
    [InlineData("contact@hubix.ro.evil.com", "*@hubix.ro", false)] // anchored, not a substring
    [InlineData("hubix.ro@example.com", "*@hubix.ro", false)]
    [InlineData("buyer@example.ro", "*@hubix.ro", false)]
    [InlineData("exact@match.com", "exact@match.com", true)]
    public void Patterns_are_anchored_and_case_insensitive(string address, string pattern, bool expected)
    {
        SafeEmailSender.Matches(address, pattern).ShouldBe(expected);
    }

    // --- What became of it -----------------------------------------------------------
    // The settings screen reports these words back to an operator, so they are part of the
    // behaviour rather than an implementation detail.

    [Fact]
    public async Task A_kept_message_says_why_it_was_kept()
    {
        var (sender, _, _) = Build();

        var delivery = await sender.DeliverAsync(To("buyer@example.ro"), CancellationToken.None);

        delivery.Transmitted.ShouldBeFalse();
        delivery.DivertedBecause.ShouldBe("live sending is disabled");
        delivery.To.ShouldBe("buyer@example.ro");
    }

    [Fact]
    public async Task A_blocked_message_says_it_was_the_block_list()
    {
        var (sender, _, _) = Build(o =>
        {
            o.LiveSendingEnabled = true;
            o.BlockedRecipients = ["*@hubix.ro"];
        });

        var delivery = await sender.DeliverAsync(To("anyone@hubix.ro"), CancellationToken.None);

        delivery.Transmitted.ShouldBeFalse();
        delivery.DivertedBecause.ShouldBe("recipient is on the blocked list");
    }

    [Fact]
    public async Task A_transmitted_message_says_so_and_names_where_it_really_went()
    {
        var (sender, _, _) = Build(o =>
        {
            o.LiveSendingEnabled = true;
            o.RedirectAllTo = "tester@example.ro";
        });

        var delivery = await sender.DeliverAsync(To("buyer@example.ro"), CancellationToken.None);

        delivery.Transmitted.ShouldBeTrue();
        delivery.DivertedBecause.ShouldBeNull();

        // The redirect address, not the one it was addressed to: that is what an operator
        // needs to know to go and look for it.
        delivery.To.ShouldBe("tester@example.ro");
    }

    /// <summary>
    /// The two defaults that are now the only copy: neither is set in appsettings any more,
    /// because a configured list appends to this rather than replacing it. If somebody removes
    /// these initialisers, an unconfigured instance starts sending, and to Hubix.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_instance_sends_nothing_and_blocks_Hubix()
    {
        var bare = new EmailOptions();

        bare.LiveSendingEnabled.ShouldBeFalse();
        bare.BlockedRecipients.ShouldBe(["*@hubix.ro"]);

        // And the guard agrees, which is the part that matters.
        var (sender, outbox, live) = Build();

        await sender.SendAsync(To("anyone@hubix.ro"), CancellationToken.None);

        live.Sent.ShouldBeEmpty();
        outbox.Sent.Count.ShouldBe(1);
    }
}
