using System.Globalization;
using Keystone.Domain.Entities;
using Keystone.Infrastructure.Email;
using Keystone.Infrastructure.Sales;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Keystone.Web.Notifications;

public sealed class KeystoneOptions
{
    public const string Section = "Keystone";

    /// <summary>Absolute base for links in emails. Relative URLs are useless in an inbox.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5001";

    /// <summary>
    /// Where new-hold notifications go internally.
    /// </summary>
    /// <remarks>
    /// Defaults to a <c>.invalid</c> address on purpose. RFC 2606 reserves that TLD so it can
    /// never resolve, which means a misconfigured environment bounces instead of emailing a
    /// real inbox. Set the real address in configuration for production only.
    /// </remarks>
    public string SalesInbox { get; set; } = "sales@keystone.invalid";
}

/// <summary>
/// Composes the emails a hold produces.
/// </summary>
/// <remarks>
/// Lives in the web project rather than infrastructure so it can use the same resource files
/// as the pages. Emails are written in the prospect's own language, which is the one they
/// filled the form in, not the one whoever triggered the job happens to be using.
/// </remarks>
public sealed class HoldNotifier(
    IEmailSender email,
    IStringLocalizer<SharedResource> localizer,
    IOptionsSnapshot<KeystoneOptions> options,
    SalesInboxResolver inboxes,
    WaitlistService waitlist)
{
    private readonly KeystoneOptions _options = options.Value;

    /// <summary>
    /// Asks whoever owns the address to confirm that they are the one reserving.
    /// </summary>
    /// <remarks>
    /// Nothing is held until this link comes back, so this is the one email in the funnel
    /// that goes to an address nobody has shown is theirs. It says plainly that ignoring it
    /// reserves nothing, because the person reading it may not be the person who typed it.
    /// </remarks>
    public async Task ReserveConfirmAsync(
        string toEmail,
        string locale,
        Guid projectId,
        string projectName,
        string unitCode,
        string token,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var link = $"{_options.PublicBaseUrl.TrimEnd('/')}/reserve/{token}";

        var body = InLocale(locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.ReserveConfirmBody"].Value,
            unitCode,
            projectName,
            link,
            Display.Moment(expiresAt, "dd MMM yyyy HH:mm", localizer)));

        var subject = InLocale(locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.ReserveConfirmSubject"].Value,
            unitCode,
            projectName));

        await email.SendAsync(
            new EmailMessage(toEmail, subject, body, await inboxes.ForAsync(projectId, ct)), ct);
    }

    public async Task HoldConfirmedAsync(
        string toEmail,
        string locale,
        Guid projectId,
        string projectName,
        string unitCode,
        DateTimeOffset expiresAt,
        int holdHours,
        string accessToken,
        CancellationToken ct)
    {
        var body = InLocale(locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.HoldBody"].Value,
            unitCode,
            projectName,
            holdHours,
            Display.Moment(expiresAt, "dd MMMM yyyy, HH:mm", localizer),
            $"{_options.PublicBaseUrl.TrimEnd('/')}/hold/{accessToken}"));

        var subject = InLocale(locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.HoldSubject"].Value,
            unitCode,
            projectName));

        await email.SendAsync(
            new EmailMessage(toEmail, subject, body, await inboxes.ForAsync(projectId, ct)), ct);
    }

    public async Task HoldExpiredAsync(ExpiredHold hold, CancellationToken ct)
    {
        var body = InLocale(hold.ProspectLocale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.ExpiredBody"].Value,
            hold.UnitCode,
            hold.ProjectName,
            $"{_options.PublicBaseUrl.TrimEnd('/')}/projects/{hold.ProjectSlug}/apartments"));

        var subject = InLocale(hold.ProspectLocale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.ExpiredSubject"].Value,
            hold.UnitCode));

        await email.SendAsync(new EmailMessage(hold.ProspectEmail, subject, body), ct);
    }

    /// <summary>
    /// Confirms a place in the queue.
    /// </summary>
    /// <remarks>
    /// Carries the link to their own page, which is where they can leave. An email telling
    /// somebody they are on a list has to also be the way off it.
    /// <para>
    /// Takes the entry rather than an address and a token, and reads both itself: the link
    /// goes to the address on file, and the form that triggered this never holds it.
    /// </para>
    /// </remarks>
    public async Task WaitlistJoinedAsync(
        Guid entryId,
        string locale,
        Guid projectId,
        string projectName,
        string? typeLabel,
        int place,
        CancellationToken ct)
    {
        var entry = await waitlist.LinkForAsync(entryId, ct);
        var link = $"{_options.PublicBaseUrl.TrimEnd('/')}/waitlist/{entry.AccessToken}";

        var body = InLocale(locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.WaitlistJoinedBody"].Value,
            typeLabel ?? localizer["Waitlist.AnyType"].Value,
            projectName,
            place,
            link));

        var subject = InLocale(locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.WaitlistJoinedSubject"].Value,
            projectName));

        await email.SendAsync(
            new EmailMessage(entry.Email, subject, body, await inboxes.ForAsync(projectId, ct)), ct);
    }

    public async Task UnitFreedAsync(
        WaitlistCandidate candidate,
        string projectName,
        string projectSlug,
        string unitCode,
        CancellationToken ct)
    {
        var body = InLocale(candidate.Locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.WaitlistBody"].Value,
            unitCode,
            projectName,
            $"{_options.PublicBaseUrl.TrimEnd('/')}/projects/{projectSlug}/apartments"));

        var subject = InLocale(candidate.Locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.WaitlistSubject"].Value,
            projectName));

        await email.SendAsync(new EmailMessage(candidate.Email, subject, body), ct);
    }

    /// <summary>Notifies staff. Always English — it is an internal message.</summary>
    /// <param name="parkingSpaces">
    /// Space codes claimed with the apartment, if any. Sales need these: the spaces are off the
    /// market under the same countdown, and nothing else tells them which.
    /// </param>
    public async Task SalesNotifiedAsync(
        Guid projectId,
        string projectName,
        string unitCode,
        string prospectEmail,
        string? prospectName,
        string? phone,
        DateTimeOffset expiresAt,
        IReadOnlyList<string> parkingSpaces,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parkingSpaces);

        var parking = parkingSpaces.Count == 0
            ? ""
            : $"\nParking: {string.Join(", ", parkingSpaces)}\n";

        // Romanian time, like the buyer's own email. Sales ring the buyer about this deadline,
        // and the two of them reading different numbers for the same moment helps nobody.
        var expires = Display.InRomania(expiresAt)
            .ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);

        var body =
            $"""
             {prospectName ?? "(no name given)"} <{prospectEmail}>{(phone is null ? "" : $"  {phone}")}
             has taken a hold on {unitCode}, {projectName}.
             {parking}
             It expires {expires} (Romania time) unless they proceed.
             """;

        await email.SendAsync(
            new EmailMessage(
                await inboxes.ForAsync(projectId, ct),
                $"Hold: {unitCode} — {projectName}",
                body,
                prospectEmail),
            ct);
    }

    /// <summary>
    /// Runs a lambda under a given culture, then restores it.
    /// </summary>
    /// <remarks>
    /// Background jobs have no request culture, and a prospect who filled in a Romanian form
    /// must get a Romanian email even when the job that sends it was triggered by nobody.
    /// </remarks>
    private static string InLocale(string locale, Func<string> compose)
    {
        var previous = CultureInfo.CurrentUICulture;

        try
        {
            var culture = Cultures.IsSupported(locale)
                ? new CultureInfo(locale)
                : new CultureInfo(Cultures.English);

            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;

            return compose();
        }
        catch (CultureNotFoundException)
        {
            return compose();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
            CultureInfo.CurrentCulture = previous;
        }
    }
}
