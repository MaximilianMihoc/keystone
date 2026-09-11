using System.Globalization;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.SqlServer;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Hubix.Infrastructure.Auth;
using Hubix.Infrastructure.Email;
using Hubix.Infrastructure.Assets;
using Hubix.Infrastructure.Import;
using Hubix.Infrastructure.Sales;
using Hubix.Infrastructure.Settings;
using Hubix.Web;
using Hubix.Web.Api;
using Hubix.Infrastructure.Privacy;
using Hubix.Web.Health;
using Hubix.Web.Jobs;
using Hubix.Web.Pages.Admin;
using Hubix.Web.Notifications;
using Hubix.Web.Settings;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Resource files live in Resources/, keyed by the marker type SharedResource.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services
    .AddRazorPages()
    .AddViewLocalization()
    // Every ErrorMessage on a page model is a key into SharedResource. Without this, the
    // default provider looks for a resx named after the model type — Reserve+InputModel —
    // finds none, and renders the key itself, so a buyer who leaves a box empty is told
    // "Validation.SurnameRequired".
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));

builder.Services.AddHubixInfrastructure(builder.Configuration);

builder.Services.Configure<HubixOptions>(builder.Configuration.GetSection(HubixOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<HoldService>();
builder.Services.AddScoped<WaitlistService>();
builder.Services.AddScoped<EnquiryService>();
builder.Services.AddScoped<HoldNotifier>();
builder.Services.AddScoped<EnquiryNotifier>();
builder.Services.AddScoped<SalesInboxResolver>();
builder.Services.AddScoped<HoldExpiryJob>();
builder.Services.AddScoped<StaffAuthService>();
builder.Services.AddScoped<StaffNotifier>();
builder.Services.AddScoped<UnitAdminService>();
builder.Services.AddScoped<ProjectAdminService>();
builder.Services.AddScoped<PricingAdminService>();
builder.Services.AddScoped<StaffAdminService>();
builder.Services.AddScoped<ProspectAdminService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<ParkingAdminService>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddScoped<WaitlistPromoter>();
// --- Asset storage -----------------------------------------------------------------
// A folder unless blob storage is configured. Both sign their read links and expire them,
// so nothing above this line can tell which one it is talking to — and a difference between
// them shows up locally rather than in production.
builder.Services.Configure<AssetOptions>(builder.Configuration.GetSection(AssetOptions.Section));

builder.Services.AddSingleton<IAssetStorage>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<AssetOptions>>();

    return string.IsNullOrWhiteSpace(settings.Value.BlobConnectionString)
        ? new LocalAssetStorage(settings, sp.GetRequiredService<TimeProvider>())
        : new BlobAssetStorage(settings);
});

builder.Services.AddScoped<AssetService>();
builder.Services.AddScoped<PrivacyService>();

// --- Health ------------------------------------------------------------------------
// Poll /health for up-or-down and /health/detail for why. The check worth alerting on is
// hold expiry: a dead job is invisible from outside, and the first person to notice would
// be a buyer told an apartment was taken when it was not.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<HoldExpiryHealthCheck>("hold-expiry", tags: ["ready", "jobs"])
    .AddCheck<AssetStorageHealthCheck>("asset-storage", tags: ["ready"]);

builder.Services.AddScoped<ImportPlanner>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<InventoryExporter>();

// --- Staff authentication ----------------------------------------------------------
// Passwordless, for a team of four. No password store means no hashing policy to get
// wrong, no reset flow to build, and nothing worth stealing if the database leaks.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "hubix.staff";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.LoginPath = "/admin/login";
        options.LogoutPath = "/admin/logout";
        options.AccessDeniedPath = "/admin/login";

        // The admin client talks JSON. Redirecting its fetch calls to an HTML login page
        // would surface as a parse error rather than "your session ended".
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.Staff, policy => policy.RequireAuthenticatedUser())
    .AddPolicy(AuthPolicies.CanEdit, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole(nameof(StaffRole.Admin), nameof(StaffRole.Sales)))
    .AddPolicy(AuthPolicies.Admin, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole(nameof(StaffRole.Admin)));

// --- Email -----------------------------------------------------------------------
// Every message goes through SafeEmailSender, which transmits only when live sending is
// explicitly enabled and the recipient is neither blocked nor outside the allowlist.
// Everything else lands in the local outbox, written exactly as it would have been sent.
//
// Hubix has not been approached about this system. Their addresses are on the blocked
// list by default, so wiring up a provider cannot introduce the project to them by
// emailing them a reservation notice.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));

// Saved settings win over the file, for both of these. See SettingsFromDatabase.
builder.Services.AddScoped<IPostConfigureOptions<EmailOptions>, EmailSettingsFromDatabase>();
builder.Services.AddScoped<IPostConfigureOptions<HubixOptions>, HubixSettingsFromDatabase>();

builder.Services.AddSingleton<LoggingEmailSender>(sp => new LoggingEmailSender(
    sp.GetRequiredService<ILogger<LoggingEmailSender>>(),
    Path.Combine(builder.Environment.ContentRootPath, "..", "..", "outbox")));

builder.Services.AddScoped<IEmailSender>(sp => new SafeEmailSender(
    outbox: sp.GetRequiredService<LoggingEmailSender>(),

    // The snapshot, not IOptions: it is rebuilt per scope, so the guards a settings screen
    // changed are the guards the next message is checked against.
    options: sp.GetRequiredService<IOptionsSnapshot<EmailOptions>>(),
    logger: sp.GetRequiredService<ILogger<SafeEmailSender>>(),

    // No real provider yet. Register one here when there is, and it still cannot reach
    // anyone the options above do not permit.
    live: null));

// --- Background jobs --------------------------------------------------------------
// Hangfire persists in the same database, so the schedule survives restarts, retries on
// failure, and takes a distributed lock rather than double-firing across instances.
var connectionString = builder.Configuration.GetConnectionString(
    DependencyInjection.ConnectionStringName);

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        PrepareSchemaIfNecessary = true,
        QueuePollInterval = TimeSpan.FromSeconds(15),
        DisableGlobalLocks = true
    }));

builder.Services.AddHangfireServer(options => options.WorkerCount = 2);

// --- Rate limiting ----------------------------------------------------------------
// Taking a hold removes a unit from sale for two days, so the endpoint that does it is
// worth protecting from a script.
//
// Submissions only. A Razor Page is a single endpoint for every handler it has, so a policy
// attached to the page counts its GETs as well — which spent the whole allowance on people
// reading the form, and answered the fifth page view with a 429 before anybody had submitted
// anything. Reading a form is not the act worth limiting; sending it is.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A bare 429 has no body, so a blocked visitor gets a blank page and reads it as the site
    // being broken rather than as "you were too quick". 303 rather than 302: the request that
    // was refused is usually a form post, and the browser has to follow it with a GET.
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.HttpContext.Response.Headers.Location = "/busy";
        return ValueTask.CompletedTask;
    };

    options.AddPolicy("reserve", context =>
        !HttpMethods.IsPost(context.Request.Method)
            ? RateLimitPartition.GetNoLimiter<string>("read")
            : RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(10)
                }));

    // Sign-in links are emailed, so a script hammering this endpoint fills an inbox.
    options.AddPolicy("login", context =>
        !HttpMethods.IsPost(context.Request.Method)
            ? RateLimitPartition.GetNoLimiter<string>("read")
            : RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(15)
                }));
});

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

// A 404 otherwise arrives with an empty body, and the browser fills it with its own "cannot
// be reached" page — which reads as the site being down rather than as one wrong address.
// Re-executed rather than redirected, so the caller still receives the real status code: a
// search engine told 200 for a missing page keeps the dead link.
//
// Not for /api. The admin client reads JSON, and several endpoints answer with a bare
// Results.NotFound(); handing those an HTML page would break the parse instead of the page.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    branch => branch.UseStatusCodePagesWithReExecute("/Error"));

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRequestLocalization();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Up-or-down, no detail, no authentication: a monitor has to be able to poll it, and what
// is running where is nobody else's business.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = (context, _) =>
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync("ok");
    }
});

// Why, in detail, for staff and for whatever is alerting.
app.MapHealthChecks("/health/detail", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data
            })
        });
    }
}).RequireAuthorization(AuthPolicies.Staff);

app.MapRazorPages();
app.MapAdminApi();
app.MapAssetFiles();

// Every minute. Cheap when there is nothing due, and a two-day countdown does not need
// finer granularity than this.
//
// Registered through IRecurringJobManager from DI rather than the static RecurringJob
// helper: the static API reads JobStorage.Current, which is not set during startup.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider
        .GetRequiredService<IRecurringJobManager>()
        .AddOrUpdate<HoldExpiryJob>(
            HoldExpiryJob.RecurringJobId,
            job => job.RunAsync(CancellationToken.None),
            "* * * * *");
}

// Last, so a production instance that is not fit to serve anybody refuses to start rather
// than starting and being wrong quietly.
ProductionReadiness.EnsureReady(app);

app.Run();
