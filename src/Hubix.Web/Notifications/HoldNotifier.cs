using System.Globalization;
using Hubix.Domain.Entities;
using Hubix.Infrastructure.Email;
using Hubix.Infrastructure.Sales;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Hubix.Web.Notifications;

public sealed class HubixOptions
{
    public const string Section = "Hubix";

    /// <summary>Absolute base for links in emails. Relative URLs are useless in an inbox.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5199";

    /// <summary>Where new-hold notifications go internally.</summary>
    public string SalesInbox { get; set; } = "contact@hubix.ro";
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
    IOptions<HubixOptions> options)
{
    private readonly HubixOptions _options = options.Value;

    public async Task HoldConfirmedAsync(
        string toEmail,
        string locale,
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
            expiresAt.ToLocalTime().ToString("dd MMMM yyyy, HH:mm", CultureInfo.CurrentCulture),
            $"{_options.PublicBaseUrl.TrimEnd('/')}/hold/{accessToken}"));

        var subject = InLocale(locale, () => string.Format(
            CultureInfo.CurrentCulture,
            localizer["Email.HoldSubject"].Value,
            unitCode,
            projectName));

        await email.SendAsync(new EmailMessage(toEmail, subject, body, _options.SalesInbox), ct);
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
    public async Task SalesNotifiedAsync(
        string projectName,
        string unitCode,
        string prospectEmail,
        string? prospectName,
        string? phone,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var body =
            $"""
             {prospectName ?? "(no name given)"} <{prospectEmail}>{(phone is null ? "" : $"  {phone}")}
             has taken a hold on {unitCode}, {projectName}.

             It expires {expiresAt:dd MMM yyyy HH:mm} UTC unless they proceed.
             """;

        await email.SendAsync(
            new EmailMessage(
                _options.SalesInbox,
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
