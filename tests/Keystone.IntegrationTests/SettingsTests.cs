using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Email;
using Keystone.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// The email settings, saved rather than deployed.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class SettingsTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static SettingsService Service(KeystoneDbContext db) =>
        new(db, NullLogger<SettingsService>.Instance);

    private static EmailSettingsInput Input(
        bool live = false,
        string? redirect = null,
        string[]? allowed = null,
        string[]? blocked = null) =>
        new("sales@example.test", "https://portal.example.test", live, redirect,
            allowed ?? [], blocked ?? ["*@hubix.ro"]);

    private async Task ClearAsync()
    {
        await using var db = fixture.CreateContext();
        await db.PortalSettings.ExecuteDeleteAsync();
        await db.AuditLogs.ExecuteDeleteAsync();
    }

    /// <summary>The settings as the running application would see them after a save.</summary>
    private EmailOptions Effective(KeystoneDbContext db, EmailOptions fromConfiguration)
    {
        new EmailSettingsFromDatabase(db).PostConfigure(null, fromConfiguration);
        return fromConfiguration;
    }

    [Fact]
    public async Task Nothing_saved_leaves_the_configuration_file_in_charge()
    {
        await ClearAsync();

        await using var db = fixture.CreateContext();

        var configured = new EmailOptions
        {
            LiveSendingEnabled = true,
            RedirectAllTo = "dev@example.test",
            BlockedRecipients = ["*@hubix.ro"]
        };

        var effective = Effective(db, configured);

        effective.LiveSendingEnabled.ShouldBeTrue();
        effective.RedirectAllTo.ShouldBe("dev@example.test");
    }

    [Fact]
    public async Task A_saved_setting_is_what_the_next_message_is_checked_against()
    {
        await ClearAsync();

        await using (var write = fixture.CreateContext())
        {
            await Service(write).SaveAsync(
                Input(live: true, allowed: ["*@example.test"], blocked: ["*@hubix.ro"]),
                Actor, "admin@example.test", CancellationToken.None);
        }

        await using var db = fixture.CreateContext();

        // Configuration says the opposite of everything saved, and loses.
        var effective = Effective(db, new EmailOptions
        {
            LiveSendingEnabled = false,
            RedirectAllTo = "dev@example.test",
            AllowedRecipients = [],
            BlockedRecipients = []
        });

        effective.LiveSendingEnabled.ShouldBeTrue();
        effective.RedirectAllTo.ShouldBeNull();
        effective.AllowedRecipients.ShouldBe(["*@example.test"]);
        effective.BlockedRecipients.ShouldBe(["*@hubix.ro"]);

        // And the sender obeys the saved values, which is the only part that matters: a
        // blocked address stays blocked however the switches were turned on.
        var outbox = new CountingSender();
        var live = new CountingSender();

        var sender = new SafeEmailSender(
            outbox, Options.Create(effective), NullLogger<SafeEmailSender>.Instance, live);

        await sender.SendAsync(new EmailMessage("someone@hubix.ro", "s", "b"), CancellationToken.None);
        await sender.SendAsync(new EmailMessage("buyer@example.test", "s", "b"), CancellationToken.None);

        live.Count.ShouldBe(1);
        outbox.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Every_save_records_what_it_changed_and_who_changed_it()
    {
        await ClearAsync();

        await using (var first = fixture.CreateContext())
        {
            await Service(first).SaveAsync(Input(), Actor, "admin@example.test", CancellationToken.None);
        }

        await using (var second = fixture.CreateContext())
        {
            await Service(second).SaveAsync(
                Input(live: true, allowed: ["*@example.test"]),
                Actor, "admin@example.test", CancellationToken.None);
        }

        await using var db = fixture.CreateContext();

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == "settings")
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        rows.Count.ShouldBe(2);
        rows[0].Action.ShouldBe("settings.email.changed");
        rows[0].ActorLabel.ShouldBe("admin@example.test");

        // The first save had nothing to compare against; the second says what it replaced,
        // which is the question asked afterwards about a message that should not have gone.
        rows[0].Before.ShouldBeNull();
        rows[1].Before.ShouldNotBeNull();
        rows[1].Before!.ShouldContain("\"LiveSendingEnabled\":false");
        rows[1].After!.ShouldContain("\"LiveSendingEnabled\":true");

        // One row, however many times it is saved.
        (await db.PortalSettings.CountAsync()).ShouldBe(1);
    }

    private sealed class CountingSender : IEmailSender
    {
        public int Count { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
