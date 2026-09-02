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
    [InlineData("fr-FR", false)]
    [InlineData("en", false)] // not a supported culture id, though it resolves by fallback
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_configured_cultures_are_accepted_from_untrusted_input(string? culture, bool supported)
    {
        Cultures.IsSupported(culture).ShouldBe(supported);
    }
}
