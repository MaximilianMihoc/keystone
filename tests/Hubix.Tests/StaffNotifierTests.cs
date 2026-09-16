using Hubix.Infrastructure.Auth;
using Hubix.Infrastructure.Email;
using Hubix.Web.Notifications;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Hubix.Tests;

/// <summary>
/// The sign-in link, which is a live credential for twelve hours of admin.
/// </summary>
/// <remarks>
/// It is built from configuration and never from the request. A link built from the request's
/// Host header lets anybody who can post the login form send a genuine member of staff a link
/// pointing at their own server, and collect the token when it is followed (issue #33).
/// </remarks>
public class StaffNotifierTests
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

    private static readonly LoginLink Link = new(
        "a-token", "dev@hubix.invalid", "Dev", DateTimeOffset.UtcNow.AddMinutes(15));

    /// <summary>The notifier reads a snapshot, because the settings are laid over from the database.</summary>
    private sealed class Snapshot(HubixOptions value) : IOptionsSnapshot<HubixOptions>
    {
        public HubixOptions Value => value;

        public HubixOptions Get(string? name) => value;
    }

    private static (StaffNotifier Notifier, RecordingSender Outbox) Build(string baseUrl)
    {
        var outbox = new RecordingSender();

        return (
            new StaffNotifier(outbox, new Snapshot(new HubixOptions { PublicBaseUrl = baseUrl })),
            outbox);
    }

    [Fact]
    public async Task The_link_is_built_from_the_configured_address()
    {
        var (notifier, outbox) = Build("https://portal.example.ro");

        var url = await notifier.SignInLinkAsync(Link, CancellationToken.None);

        url.ShouldBe("https://portal.example.ro/admin/login/verify?token=a-token");
        outbox.Sent.ShouldHaveSingleItem().Body.ShouldContain(url);
    }

    [Fact]
    public async Task Nothing_a_visitor_sends_can_change_where_the_link_points()
    {
        // There is nowhere for a Host header to enter: the notifier is given a token and a
        // configured address, and builds the rest itself.
        var (notifier, outbox) = Build("https://portal.example.ro");

        await notifier.SignInLinkAsync(Link, CancellationToken.None);

        outbox.Sent.ShouldHaveSingleItem().Body.ShouldNotContain("attacker");
        outbox.Sent[0].Body.ShouldContain("https://portal.example.ro/");
    }

    [Fact]
    public async Task A_trailing_slash_in_configuration_does_not_double_up()
    {
        var (notifier, _) = Build("https://portal.example.ro/");

        (await notifier.SignInLinkAsync(Link, CancellationToken.None))
            .ShouldBe("https://portal.example.ro/admin/login/verify?token=a-token");
    }
}
