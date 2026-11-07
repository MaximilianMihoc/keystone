using Keystone.Infrastructure.Admin;
using Shouldly;

namespace Keystone.Tests;

/// <summary>
/// Turning a project's name into the address it lives at.
/// </summary>
/// <remarks>
/// The diacritic cases are the point of this file. Romanian project names carry ș, ț, ă, â and
/// î, and a slug that keeps them produces a URL full of percent-escapes that nobody can read
/// back to a project — or worse, one that a mail client mangles when somebody shares it.
/// </remarks>
public class SlugTests
{
    [Theory]
    [InlineData("Roka Park View", "roka-park-view")]
    [InlineData("București Park", "bucuresti-park")]
    [InlineData("Cartierul Vienez", "cartierul-vienez")]
    [InlineData("Ap. 101 / B", "ap-101-b")]
    [InlineData("  Roka   Park  ", "roka-park")]
    [InlineData("Prima Solis (Etapa 2)", "prima-solis-etapa-2")]
    [InlineData("Brașov — Coresi", "brasov-coresi")]
    public void Turns_a_name_into_an_address(string name, string expected) =>
        ProjectAdminService.Slugify(name).ShouldBe(expected);

    [Fact]
    public void Every_Romanian_letter_folds_to_ascii() =>
        ProjectAdminService.Slugify("ăâîșț ĂÂÎȘȚ").ShouldBe("aaist-aaist");

    [Fact]
    public void A_name_with_no_letters_makes_no_address() =>
        // The caller refuses this rather than inventing one, so it has to come back empty
        // rather than as a hyphen or a placeholder.
        ProjectAdminService.Slugify("— … !").ShouldBe("");

    [Fact]
    public void A_long_name_fits_the_column()
    {
        var slug = ProjectAdminService.Slugify(string.Join(" ", Enumerable.Repeat("verylongword", 20)));

        slug.Length.ShouldBeLessThanOrEqualTo(120);
        slug.ShouldNotEndWith("-");
    }
}
