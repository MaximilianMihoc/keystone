using System.Text.Json;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Settings;

/// <summary>The email settings as a screen sends them in, before anything is saved.</summary>
public sealed record EmailSettingsInput(
    string? SalesInbox,
    string? PublicBaseUrl,
    bool LiveSendingEnabled,
    string? RedirectAllTo,
    IReadOnlyList<string> AllowedRecipients,
    IReadOnlyList<string> BlockedRecipients);

/// <summary>
/// Reading and writing the one settings row.
/// </summary>
/// <remarks>
/// Every save is audited, including what the values were before it. These settings decide
/// whether mail leaves the machine and who it may reach, so "who changed this, when, and from
/// what" is not a nicety — it is the only way to answer for a message that should not have
/// been sent.
/// </remarks>
public sealed class SettingsService(HubixDbContext db, ILogger<SettingsService> logger)
{
    public Task<PortalSettings?> CurrentAsync(CancellationToken ct) =>
        db.PortalSettings.AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);

    public async Task<PortalSettings> SaveAsync(
        EmailSettingsInput input,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var row = await db.PortalSettings.OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        var before = row is null ? null : Describe(row);

        if (row is null)
        {
            // First save. Until now the file was the whole answer; from here the row is.
            row = new PortalSettings();
            db.PortalSettings.Add(row);
        }

        row.SalesInbox = Clean(input.SalesInbox);
        row.PublicBaseUrl = Clean(input.PublicBaseUrl);
        row.LiveSendingEnabled = input.LiveSendingEnabled;
        row.RedirectAllTo = Clean(input.RedirectAllTo);
        row.AllowedRecipients = Patterns(input.AllowedRecipients);
        row.BlockedRecipients = Patterns(input.BlockedRecipients);

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "settings",
            EntityId = row.Id,
            Action = "settings.email.changed",
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = JsonSerializer.Serialize(Describe(row))
        });

        await db.SaveChangesAsync(ct);

        // Loud on purpose. Turning this on is the moment this system stops being a sandbox,
        // and it should be findable in the log without knowing to look for it.
        if (row.LiveSendingEnabled)
        {
            logger.LogWarning(
                "Live email sending is ON. Set by {Actor}. Blocked: {Blocked}. Allowed: {Allowed}.",
                actorLabel,
                row.BlockedRecipients.Count == 0 ? "nobody" : string.Join(", ", row.BlockedRecipients),
                row.AllowedRecipients.Count == 0 ? "everyone" : string.Join(", ", row.AllowedRecipients));
        }
        else
        {
            logger.LogInformation("Email settings changed by {Actor}. Live sending is off.", actorLabel);
        }

        return row;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <remarks>
    /// Deduplicated, because the same pattern twice is the same rule twice — whether it got
    /// that way from somebody typing it twice or from a configured list being appended to the
    /// property default rather than replacing it. Saving a duplicate would make it permanent.
    /// </remarks>
    private static List<string> Patterns(IReadOnlyList<string> values) =>
        [.. values
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static object Describe(PortalSettings row) => new
    {
        row.SalesInbox,
        row.PublicBaseUrl,
        row.LiveSendingEnabled,
        row.RedirectAllTo,
        row.AllowedRecipients,
        row.BlockedRecipients
    };
}
