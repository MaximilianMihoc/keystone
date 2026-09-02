using System.Globalization;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.SqlServer;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Email;
using Hubix.Infrastructure.Sales;
using Hubix.Web.Jobs;
using Hubix.Web.Notifications;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Resource files live in Resources/, keyed by the marker type SharedResource.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services
    .AddRazorPages()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();

builder.Services.AddHubixInfrastructure(builder.Configuration);

builder.Services.Configure<HubixOptions>(builder.Configuration.GetSection(HubixOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<HoldService>();
builder.Services.AddScoped<WaitlistService>();
builder.Services.AddScoped<HoldNotifier>();
builder.Services.AddScoped<HoldExpiryJob>();

// Development sender: writes messages to logs and to an outbox folder, and sends nothing.
// Swap for a real provider before this reaches a buyer.
builder.Services.AddSingleton<IEmailSender>(sp => new LoggingEmailSender(
    sp.GetRequiredService<ILogger<LoggingEmailSender>>(),
    Path.Combine(builder.Environment.ContentRootPath, "..", "..", "outbox")));

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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("reserve", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10)
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

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRequestLocalization();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthorization();
app.MapRazorPages();

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

app.Run();
