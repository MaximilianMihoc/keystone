using Keystone.Infrastructure;
using Keystone.Infrastructure.Settings;
using Keystone.Web.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Keystone.Web.Settings;

/// <summary>
/// The addresses, saved rather than deployed.
/// </summary>
/// <remarks>
/// Here rather than beside <see cref="EmailSettingsFromDatabase"/> only because
/// <see cref="KeystoneOptions"/> is declared in this project. Same rules: scoped, read through
/// <c>IOptionsSnapshot</c>, and one row or none.
/// </remarks>
public sealed class KeystoneSettingsFromDatabase(KeystoneDbContext db) : IPostConfigureOptions<KeystoneOptions>
{
    public void PostConfigure(string? name, KeystoneOptions options)
    {
        var saved = db.PortalSettings.AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefault();

        if (saved is null)
        {
            return;
        }

        // Unlike the sending guards, a blank here is "not set" rather than an answer: neither
        // of these has a meaningful empty value, and falling back to the file beats sending
        // mail addressed to nobody with links pointing nowhere.
        if (!string.IsNullOrWhiteSpace(saved.SalesInbox))
        {
            options.SalesInbox = saved.SalesInbox;
        }

        if (!string.IsNullOrWhiteSpace(saved.PublicBaseUrl))
        {
            options.PublicBaseUrl = saved.PublicBaseUrl;
        }
    }
}
