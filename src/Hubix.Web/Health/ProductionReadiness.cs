using Hubix.Infrastructure.Assets;
using Hubix.Infrastructure.Email;
using Hubix.Web.Notifications;
using Microsoft.Extensions.Options;

namespace Hubix.Web.Health;

/// <summary>
/// Configuration that is fine locally and dangerous in production.
/// </summary>
/// <remarks>
/// <para>
/// Checked at startup and fatal, because every one of these fails quietly otherwise. A missing
/// signing key does not break anything until the second instance starts and half the images
/// stop loading; a default sender address does not break anything until it reaches somebody's
/// inbox. A refusal to start is loud, immediate, and happens before anybody is looking at it.
/// </para>
/// <para>
/// Nothing here runs outside Production. Development is meant to have blank keys and a local
/// folder, and a nag on every debug run is a nag people learn to ignore.
/// </para>
/// </remarks>
public static class ProductionReadiness
{
    /// <summary>Every reason this configuration is not fit to serve real people.</summary>
    public static IReadOnlyList<string> Problems(
        EmailOptions email,
        AssetOptions assets,
        HubixOptions hubix,
        string? connectionString,
        ResendOptions? provider = null)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            problems.Add("No database connection string is configured.");
        }

        problems.AddRange(EmailProblems(email));

        // Live sending with no provider behind it is the quietest failure of the lot: every
        // message falls back to the outbox and the only sign is a warning in the log.
        if (email.LiveSendingEnabled && provider is { IsConfigured: false })
        {
            problems.Add(
                "Live email is on but Resend:ApiKey is empty, so nothing can be sent and " +
                "every message would go quietly to the outbox.");
        }

        if (hubix.PublicBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                "Hubix:PublicBaseUrl still points at localhost, so every link in every email would be dead.");
        }

        if (hubix.SalesInbox.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                "Hubix:SalesInbox is still a .invalid placeholder, so internal notifications go nowhere.");
        }

        // Blob storage signs its own URLs; the local store needs a key that outlives a restart
        // and is shared between instances.
        if (string.IsNullOrWhiteSpace(assets.BlobConnectionString)
            && string.IsNullOrWhiteSpace(assets.SigningKey))
        {
            problems.Add(
                "Assets:SigningKey is empty and there is no blob storage. File links would break " +
                "on every restart and would not work across instances.");
        }

        return problems;
    }

    /// <summary>
    /// Why this combination of sending guards is not fit for production.
    /// </summary>
    /// <remarks>
    /// Its own method because these are now editable from the admin screen, and that screen
    /// refuses to save what this would refuse to start. A setting that can be saved happily
    /// and then bricks the next deploy is a worse trap than one that cannot be saved.
    ///
    /// Hubix have not been approached about this system. The day a real mail provider is
    /// wired up must not also be the day they find out about it from an automated email.
    /// </remarks>
    public static IReadOnlyList<string> EmailProblems(EmailOptions email)
    {
        var problems = new List<string>();

        if (!email.LiveSendingEnabled)
        {
            return problems;
        }

        if (string.IsNullOrWhiteSpace(email.From))
        {
            problems.Add(
                "Live email is on with no Email:From address. Nothing can be sent without one.");
        }

        // The provider's sandbox sender needs no domain verification, which is exactly what
        // makes it easy to leave behind. It can only send to test addresses and to whoever
        // owns the provider account, so a production instance wearing it would look configured
        // and reach nobody.
        if (email.From.EndsWith("@resend.dev", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                "Live email is on with Email:From on resend.dev, which is the provider's " +
                "sandbox sender. Production needs an address on a domain verified with them.");
        }

        if (email.AllowedRecipients.Count == 0 && string.IsNullOrWhiteSpace(email.RedirectAllTo))
        {
            problems.Add(
                "Live email is on with no allow list and no redirect. Mail would go to real buyers.");
        }

        if (email.BlockedRecipients.Count == 0)
        {
            problems.Add(
                "Live email is on with an empty blocked list. Hubix's own addresses must stay on it.");
        }

        return problems;
    }

    /// <summary>Refuses to start a production instance that is not fit to serve anybody.</summary>
    public static void EnsureReady(WebApplication app)
    {
        if (!app.Environment.IsProduction())
        {
            return;
        }

        // In a scope, and through the snapshot, because EmailOptions and HubixOptions are both
        // laid over from the database by a scoped post-configure. Resolving IOptions for either
        // from the root provider throws — and it would throw only in Production, where this is
        // the one thing standing between a bad configuration and real buyers.
        //
        // It also means the check sees the saved settings rather than only the file, which is
        // the behaviour worth having: an unsafe combination saved from the admin screen should
        // stop the next deploy exactly as an unsafe appsettings would.
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;

        var problems = Problems(
            services.GetRequiredService<IOptionsSnapshot<EmailOptions>>().Value,
            services.GetRequiredService<IOptions<AssetOptions>>().Value,
            services.GetRequiredService<IOptionsSnapshot<HubixOptions>>().Value,
            app.Configuration.GetConnectionString(
                Hubix.Infrastructure.DependencyInjection.ConnectionStringName),
            services.GetRequiredService<IOptions<ResendOptions>>().Value);

        if (problems.Count == 0)
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(ProductionReadiness));

        foreach (var problem in problems)
        {
            logger.LogCritical("Not ready for production: {Problem}", problem);
        }

        throw new InvalidOperationException(
            "This instance is not configured for production:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems.Select(p => $"  - {p}")));
    }
}
