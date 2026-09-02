using Hubix.Infrastructure.Assets;
using Hubix.Infrastructure.Email;
using Hubix.Web.Notifications;

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
        string? connectionString)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            problems.Add("No database connection string is configured.");
        }

        // Hubix have not been approached about this system. The day a real mail provider is
        // wired up must not also be the day they find out about it from an automated email.
        if (email.LiveSendingEnabled)
        {
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

    /// <summary>Refuses to start a production instance that is not fit to serve anybody.</summary>
    public static void EnsureReady(WebApplication app)
    {
        if (!app.Environment.IsProduction())
        {
            return;
        }

        var problems = Problems(
            app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>().Value,
            app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AssetOptions>>().Value,
            app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HubixOptions>>().Value,
            app.Configuration.GetConnectionString(
                Hubix.Infrastructure.DependencyInjection.ConnectionStringName));

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
