using Hubix.Domain.Entities;
using Shouldly;

namespace Hubix.Tests;

public class TranslationTests
{
    private static List<ProjectTranslation> Both() =>
    [
        new() { Culture = Cultures.Romanian, Summary = "Ansamblu rezidențial premium" },
        new() { Culture = Cultures.English, Summary = "Premium residential development" }
    ];

    [Fact]
    public void Returns_the_requested_culture()
    {
        Both().ForCulture(Cultures.English)!.Summary.ShouldBe("Premium residential development");
        Both().ForCulture(Cultures.Romanian)!.Summary.ShouldBe("Ansamblu rezidențial premium");
    }

    [Fact]
    public void City_uses_the_translated_name_for_the_culture()
    {
        List<ProjectTranslation> both =
        [
            new() { Culture = Cultures.Romanian, City = "București" },
            new() { Culture = Cultures.English, City = "Bucharest" }
        ];

        both.CityFor(Cultures.English, "București").ShouldBe("Bucharest");
        both.CityFor(Cultures.Romanian, "București").ShouldBe("București");
        both.CityFor("en", "București").ShouldBe("Bucharest");
    }

    [Fact]
    public void City_falls_back_to_the_canonical_name_rather_than_another_language()
    {
        // A city name is not prose: an English reader sees the project's own Romanian name
        // rather than whatever translation happens to exist.
        List<ProjectTranslation> romanianOnly =
            [new() { Culture = Cultures.Romanian, City = "București" }];

        romanianOnly.CityFor(Cultures.English, "București").ShouldBe("București");

        // A translation row carrying prose but no city falls back the same way.
        List<ProjectTranslation> proseOnly =
            [new() { Culture = Cultures.English, Summary = "Premium residential development" }];

        proseOnly.CityFor(Cultures.English, "București").ShouldBe("București");
        new List<ProjectTranslation>().CityFor(Cultures.English, "București").ShouldBe("București");
    }

    [Fact]
    public void Parking_prose_falls_back_to_romanian_rather_than_to_nothing()
    {
        // The opposite of CityFor above. A note explaining that parking is bought from the
        // developer is worth reading in Romanian; a blank panel tells an English reader
        // nothing at all.
        List<ProjectTranslation> romanianOnly =
        [
            new() { Culture = Cultures.Romanian, Parking = "Parcarea se cumpără direct de la dezvoltator." }
        ];

        romanianOnly.ForCulture(Cultures.English)!.Parking
            .ShouldBe("Parcarea se cumpără direct de la dezvoltator.");

        List<ProjectTranslation> both =
        [
            new() { Culture = Cultures.Romanian, Parking = "Supraterană" },
            new() { Culture = Cultures.English, Parking = "Above ground" }
        ];

        both.ForCulture(Cultures.English)!.Parking.ShouldBe("Above ground");
        both.ForCulture(Cultures.Romanian)!.Parking.ShouldBe("Supraterană");
    }

    [Theory]
    [InlineData("ro", "Ansamblu rezidențial premium")]
    [InlineData("ro-MD", "Ansamblu rezidențial premium")] // Romanian as spoken in Moldova
    [InlineData("en", "Premium residential development")]
    [InlineData("en-US", "Premium residential development")]
    public void Matches_on_language_when_the_exact_culture_is_missing(string culture, string expected)
    {
        // A browser sending Accept-Language: ro must not fall through to English.
        Both().ForCulture(culture)!.Summary.ShouldBe(expected);
    }

    [Fact]
    public void Falls_back_to_Romanian_when_no_translation_exists()
    {
        // Hubix authors in Romanian. Romanian prose beats an empty page.
        List<ProjectTranslation> romanianOnly =
            [new() { Culture = Cultures.Romanian, Summary = "Doar în română" }];

        romanianOnly.ForCulture(Cultures.English)!.Summary.ShouldBe("Doar în română");
    }

    [Fact]
    public void Falls_back_to_whatever_exists_when_even_the_source_is_missing()
    {
        List<ProjectTranslation> onlyGerman =
            [new() { Culture = "de-DE", Summary = "Nur auf Deutsch" }];

        onlyGerman.ForCulture(Cultures.English)!.Summary.ShouldBe("Nur auf Deutsch");
    }

    [Fact]
    public void Returns_null_when_there_are_no_translations_at_all()
    {
        new List<ProjectTranslation>().ForCulture(Cultures.English).ShouldBeNull();
    }

    [Fact]
    public void Strict_lookup_refuses_a_wrong_language_rather_than_guessing()
    {
        // Used where the entity has a canonical name of its own worth falling back to.
        List<ProjectTranslation> englishOnly =
            [new() { Culture = Cultures.English, Summary = "English only" }];

        englishOnly.ForCulture(Cultures.Romanian, fallBackToAny: false).ShouldBeNull();
        englishOnly.ForCulture(Cultures.Romanian, fallBackToAny: true).ShouldNotBeNull();
    }

    [Fact]
    public void Unit_type_display_falls_back_to_the_canonical_Romanian_name()
    {
        var type = new UnitType { Name = "2 camere" };
        type.Translations.Add(new UnitTypeTranslation
        {
            Culture = Cultures.English,
            DisplayName = "2 rooms"
        });

        type.DisplayName(Cultures.English).ShouldBe("2 rooms");
        type.DisplayName(Cultures.Romanian).ShouldBe("2 camere");

        // No translation at all: the canonical name is still shown, never a blank.
        new UnitType { Name = "Studio Dublu" }.DisplayName(Cultures.English).ShouldBe("Studio Dublu");
    }

    [Theory]
    [InlineData("en-GB", true)]
    [InlineData("ro-RO", true)]
    [InlineData("EN-GB", true)]
    // Neutral language codes are accepted too. Browsers are inconsistent about whether they
    // send "ro" or "ro-RO", and FallBackToParentCultures only ever walks down to a parent —
    // it never promotes "ro" to "ro-RO". Without these, a browser asking for plain Romanian
    // silently gets English.
    [InlineData("en", true)]
    [InlineData("ro", true)]
    [InlineData("fr-FR", false)]
    [InlineData("de", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_configured_cultures_are_accepted_from_untrusted_input(string? culture, bool supported)
    {
        Cultures.IsSupported(culture).ShouldBe(supported);
    }

    [Fact]
    public void The_switcher_offers_exactly_two_languages()
    {
        // Accepted carries neutral variants for negotiation; the switcher must not grow
        // four buttons because of them.
        Cultures.Offered.ShouldBe([Cultures.English, Cultures.Romanian]);
        Cultures.Accepted.Length.ShouldBeGreaterThan(Cultures.Offered.Length);
    }
}
