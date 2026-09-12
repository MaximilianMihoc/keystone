using Hubix.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hubix.Infrastructure.Settings;

/// <summary>
/// Lays the saved sending guards over the ones read from configuration.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, and reached through <see cref="IOptionsSnapshot{T}"/> rather than
/// <see cref="IOptions{T}"/>, so a change made on the settings screen applies to the next
/// message rather than the next deploy. <c>IOptions</c> is resolved once for the life of the
/// process, which is precisely the restart this exists to avoid.
/// </para>
/// <para>
/// One row, or none. Where there is no row, configuration stands exactly as written.
/// </para>
/// </remarks>
public sealed class EmailSettingsFromDatabase(HubixDbContext db) : IPostConfigureOptions<EmailOptions>
{
    public void PostConfigure(string? name, EmailOptions options)
    {
        var saved = db.PortalSettings.AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefault();

        if (saved is null)
        {
            return;
        }

        // Assigned whole, blanks included: an empty allow list means "everyone", and an
        // operator who clears the box means that rather than "keep what the file said".
        options.LiveSendingEnabled = saved.LiveSendingEnabled;
        options.RedirectAllTo = saved.RedirectAllTo;
        options.AllowedRecipients = saved.AllowedRecipients;
        options.BlockedRecipients = saved.BlockedRecipients;
    }
}
