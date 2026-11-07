using Keystone.Web;
using Shouldly;

namespace Keystone.Tests;

public class MapsLinkTests
{
    [Fact]
    public void A_projects_own_link_is_used_as_given()
    {
        Display.MapsLink("https://maps.app.goo.gl/abc123", "Roka Park View", "București")
            .ShouldBe("https://maps.app.goo.gl/abc123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_link_the_button_searches_for_the_project(string? mapsUrl)
    {
        // The operator has not given us a short link for every project, and a button that opens a
        // search for the right address beats one that opens Google's error page.
        Display.MapsLink(mapsUrl, "Roka Park View", "București")
            .ShouldBe("https://www.google.com/maps/search/?api=1&query=Roka%20Park%20View%2C%20Bucure%C8%99ti");
    }
}
