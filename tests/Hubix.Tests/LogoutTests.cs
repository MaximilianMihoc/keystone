using System.Reflection;
using Hubix.Web.Pages.Admin;
using Microsoft.AspNetCore.Mvc;
using Shouldly;

namespace Hubix.Tests;

public class LogoutTests
{
    [Fact]
    public void Sign_out_does_not_demand_an_antiforgery_token()
    {
        // The admin client posts a plain form with no token. Demanding one answered every
        // sign-out with a 400 and left the session open (issue #67).
        typeof(LogoutModel).GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void Sign_out_is_post_only()
    {
        // A GET handler would let a prefetch or a mail scanner end somebody's session.
        typeof(LogoutModel).GetMethods().ShouldNotContain(m => m.Name.StartsWith("OnGet"));
    }
}
