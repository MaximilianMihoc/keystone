using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>
/// Culture codes the application serves. English is the default; Romanian is offered
/// automatically to browsers that ask for it.
/// </summary>
public static class Cultures
{
    public const string English = "en-GB";

    public const string Romanian = "ro-RO";

    /// <summary>
    /// The culture Hubix authors content in. Used as the fallback when a translation is
    /// missing, because Romanian prose is better than an empty page.
    /// </summary>
    public const string Source = Romanian;

    /// <summary>
    /// The two languages the switcher offers. Two buttons, not four.
    /// </summary>
    public static readonly string[] Offered = [English, Romanian];

    /// <summary>
    /// Every culture the request pipeline will match, including the neutral language codes.
    /// </summary>
    /// <remarks>
    /// Browsers are inconsistent: some send <c>ro-RO</c>, others just <c>ro</c>.
    /// <c>FallBackToParentCultures</c> resolves <c>ro-RO</c> down to <c>ro</c>, but never
    /// promotes <c>ro</c> up to <c>ro-RO</c> — so without the neutral codes listed here, a
    /// browser asking for plain <c>ro</c> silently gets English.
    /// </remarks>
    public static readonly string[] Accepted = [English, "en", Romanian, "ro"];

    [Obsolete("Use Offered for the switcher or Accepted for request localisation.")]
    public static string[] Supported => Offered;

    public static bool IsSupported(string? culture) =>
        culture is not null && Accepted.Contains(culture, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Translatable prose for a project.
/// </summary>
/// <remarks>
/// A child table rather than <c>SummaryEn</c> / <c>SummaryRo</c> column pairs. Adding a third
/// language is then a data change, not a migration — which matters, because Hubix already has
/// deals in negotiation in Athens and Warsaw.
/// </remarks>
public class ProjectTranslation : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    /// <summary>An entry from <see cref="Cultures.Supported"/>.</summary>
    public required string Culture { get; set; }

    /// <summary>
    /// The city as it is written in this culture — "Bucharest" against "București". Optional:
    /// a missing one falls back to <see cref="Project.City"/>, which is Hubix's own spelling.
    /// </summary>
    public string? City { get; set; }

    public string? Summary { get; set; }

    public string? Body { get; set; }

    /// <summary>
    /// The late-delivery penalty, as prose. Varies too much between projects to be a number:
    /// Coresi is 25% plus 1,000 EUR a month, Torontal is 100%, Roka is 50%.
    /// </summary>
    public string? PenaltyClause { get; set; }

    /// <summary>
    /// What a buyer needs to know about parking on this project — typically that it is not
    /// sold with the apartment at all. The kind of space, where there is one, is on the unit.
    /// </summary>
    public string? Parking { get; set; }

    /// <summary>
    /// Whatever else a buyer should read before they commit — the notary's terms, what the
    /// reservation fee covers, which documents to bring. Shown on the reservation form of this
    /// project only, and blank on most of them.
    /// </summary>
    public string? ReservationNotice { get; set; }
}

/// <summary>
/// Display name for a unit type in one culture.
/// </summary>
/// <remarks>
/// <see cref="UnitType.Name"/> stays canonical and Romanian — it is the key the importer
/// matches on, and Hubix's own vocabulary. This only affects what a visitor reads.
/// </remarks>
public class UnitTypeTranslation : Entity
{
    public Guid UnitTypeId { get; set; }

    public UnitType? UnitType { get; set; }

    public required string Culture { get; set; }

    public required string DisplayName { get; set; }
}

/// <summary>Picks the best available translation for the current culture.</summary>
public static class TranslationExtensions
{
    /// <summary>
    /// Requested culture, then the language part alone so <c>ro</c> matches <c>ro-RO</c>.
    /// </summary>
    /// <param name="fallBackToAny">
    /// When true, an unmatched request falls through to the source culture and then to any
    /// translation at all. Right for prose, where Romanian beats an empty page. Wrong where the
    /// entity carries a canonical name of its own — a unit type should show "2 camere" rather
    /// than an English label to a Romanian reader, so that caller passes false and falls back
    /// to <see cref="UnitType.Name"/> instead.
    /// </param>
    public static T? ForCulture<T>(
        this IEnumerable<T> translations,
        string culture,
        bool fallBackToAny = true)
        where T : class
    {
        var all = translations as IReadOnlyList<T> ?? translations.ToList();
        if (all.Count == 0)
        {
            return null;
        }

        var match = Match(all, culture) ?? MatchLanguage(all, culture);
        if (match is not null)
        {
            return match;
        }

        return fallBackToAny
            ? Match(all, Cultures.Source) ?? all[0]
            : null;
    }

    /// <summary>
    /// The reservation notice for a culture, skipping rows that have none.
    /// </summary>
    /// <remarks>
    /// Per field rather than per row, unlike the prose on the project page. A project has a row
    /// for each offered language as soon as anything at all is written in it, so an admin who
    /// types this notice in Romanian and leaves the English box empty would otherwise show an
    /// English reader nothing — and this is the one piece of prose that exists because a buyer
    /// has to read it before committing.
    /// </remarks>
    public static string? ReservationNoticeFor(
        this IEnumerable<ProjectTranslation> translations,
        string culture) =>
        translations
            .Where(t => !string.IsNullOrWhiteSpace(t.ReservationNotice))
            .ForCulture(culture)
            ?.ReservationNotice;

    /// <summary>
    /// The city name for a culture, falling back to the project's canonical spelling rather
    /// than to a translation in some other language — the same rule as
    /// <see cref="UnitType.DisplayName"/>, and for the same reason.
    /// </summary>
    public static string CityFor(
        this IEnumerable<ProjectTranslation> translations,
        string culture,
        string canonicalCity) =>
        translations.ForCulture(culture, fallBackToAny: false)?.City ?? canonicalCity;

    private static T? Match<T>(IReadOnlyList<T> all, string culture) where T : class =>
        all.FirstOrDefault(t => string.Equals(CultureOf(t), culture, StringComparison.OrdinalIgnoreCase));

    private static T? MatchLanguage<T>(IReadOnlyList<T> all, string culture) where T : class
    {
        var language = culture.Split('-')[0];

        return all.FirstOrDefault(t =>
            CultureOf(t).StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(CultureOf(t), language, StringComparison.OrdinalIgnoreCase));
    }

    private static string CultureOf<T>(T translation) => translation switch
    {
        ProjectTranslation p => p.Culture,
        UnitTypeTranslation u => u.Culture,
        _ => throw new NotSupportedException($"{typeof(T).Name} is not a translation type.")
    };
}
