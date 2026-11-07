using Keystone.Domain.Common;

namespace Keystone.Domain.Entities;

/// <summary>
/// The settings staff can change without a deploy: where sales mail goes, and whether mail
/// may leave the machine at all.
/// </summary>
/// <remarks>
/// <para>
/// One row, or none. Configuration is what a fresh install starts from; once this row exists
/// it is authoritative for every field on it. A value that sometimes comes from a file and
/// sometimes from a screen is a value nobody can answer questions about, so the rule is the
/// simplest one that stays true: the database wins, and the screen shows what is really in use.
/// </para>
/// <para>
/// The sending guards are on this row deliberately, at the owner's instruction. Everything
/// leaving the system still passes <c>SafeEmailSender</c>, which reads these values rather
/// than the file, and every change here writes an audit row — "who turned live sending on,
/// and when" has to be answerable afterwards.
/// </para>
/// </remarks>
public class PortalSettings : Entity
{
    /// <summary>Where sales notifications go for projects that do not name their own inbox.</summary>
    public string? SalesInbox { get; set; }

    /// <summary>Absolute base for links in emails. A relative URL is useless in an inbox.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Whether mail may actually leave the machine.</summary>
    public bool LiveSendingEnabled { get; set; }

    /// <summary>When set, every message is re-addressed here, intended recipient kept visible.</summary>
    public string? RedirectAllTo { get; set; }

    /// <summary>Who may be contacted when live sending is on. Empty means everyone.</summary>
    public List<string> AllowedRecipients { get; set; } = [];

    /// <summary>Who is never contacted, whatever else is set. Checked before the allow list.</summary>
    public List<string> BlockedRecipients { get; set; } = [];
}
