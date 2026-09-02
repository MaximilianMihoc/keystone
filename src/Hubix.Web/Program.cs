using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Microsoft.AspNetCore.Localization;

var builder = WebApplication.CreateBuilder(args);

// Resource files live in Resources/, keyed by the marker type SharedResource.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services
    .AddRazorPages()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();

builder.Services.AddHubixInfrastructure(builder.Configuration);

// English is the default. A browser sending Accept-Language: ro gets Romanian without
// doing anything, and an explicit choice from the language switcher persists in a cookie.
//
// Provider order matters and the framework's default is the one we want:
//   1. ?culture= query string   — explicit, useful for testing and shareable links
//   2. .AspNetCore.Culture cookie — the visitor's own choice, set by /language
//   3. Accept-Language header    — what the browser asks for
// The default culture applies only when none of those resolve to a supported culture.
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(Cultures.English)
        .AddSupportedCultures(Cultures.Accepted)
        .AddSupportedUICultures(Cultures.Accepted);

    // Resolves en-US to en and ro-MD to ro, rather than dropping to the default. Note this
    // only ever walks *down* to a parent, which is why Cultures.Accepted also lists the
    // neutral codes: a browser sending plain "ro" has no parent to fall back to.
    options.FallBackToParentCultures = true;
    options.FallBackToParentUICultures = true;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRequestLocalization();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
