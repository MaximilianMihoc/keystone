using Hubix.Web;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Hubix.Tests;

public class ThemeTests
{
    private static HttpRequest RequestWith(string? cookie)
    {
        var context = new DefaultHttpContext();

        if (cookie is not null)
        {
            context.Request.Headers.Cookie = cookie;
        }

        return context.Request;
    }

    [Fact]
    public void No_choice_means_follow_the_browser()
    {
        // No attribute is written, so the stylesheet's prefers-color-scheme rules decide.
        Theme.FromRequest(RequestWith(null)).ShouldBeNull();
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void A_recorded_choice_is_returned(string theme)
    {
        Theme.FromRequest(RequestWith($"{Theme.CookieName}={theme}")).ShouldBe(theme);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("")]
    [InlineData("Dark")]
    [InlineData("dark\" onload=\"alert(1)")]
    public void Anything_else_is_ignored_rather_than_trusted(string value)
    {
        // The value reaches an html attribute. Razor encodes it, but a cookie is user input
        // and only the two values we write are worth honouring.
        Theme.FromRequest(RequestWith($"{Theme.CookieName}={value}")).ShouldBeNull();
    }

    [Fact]
    public void The_switcher_offers_both_themes_and_the_browser_default()
    {
        Theme.Offered.ShouldBe([Theme.Light, Theme.Dark, Theme.System]);
    }
}
