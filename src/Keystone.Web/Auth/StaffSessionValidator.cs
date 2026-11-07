using System.Security.Claims;
using Keystone.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Keystone.Web.Auth;

/// <summary>
/// Re-checks a staff session against the database while it is being used.
/// </summary>
/// <remarks>
/// <para>
/// Role and identity are written into the cookie at sign-in. Without this, that is the last
/// word for twelve hours: deactivating or demoting somebody changed the database and left
/// their open session untouched, so "revoke access" did not revoke access — the one property
/// an incident depends on (issue #34).
/// </para>
/// <para>
/// Not on every request. The check is a database round trip and the answer changes rarely, so
/// it runs at most once every <see cref="RecheckEvery"/> per session and the session carries
/// when it was last checked. That is the cost of revocation taking up to a couple of minutes
/// rather than being instant, which is the right trade for a team of four.
/// </para>
/// </remarks>
public static class StaffSessionValidator
{
    /// <summary>When the session began. Stamped at sign-in and never renewed.</summary>
    public const string SignedInAt = "keystone.signed-in-at";

    /// <summary>When the account behind the session was last looked up.</summary>
    public const string CheckedAt = "keystone.checked-at";

    /// <summary>How stale a check may be before the database is asked again.</summary>
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The longest a session may live, however much it is used.
    /// </summary>
    /// <remarks>
    /// The twelve-hour window slides, so a session in daily use would otherwise renew for ever
    /// and a cookie taken once would stay good as long as somebody kept using it. This is the
    /// ceiling that sliding cannot lift: after it, sign in again.
    /// </remarks>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(24);

    /// <summary>Whether a session that began then has outlived its ceiling.</summary>
    /// <remarks>
    /// A session with no stamp is one issued before this existed. Ending it is a single
    /// sign-in for a team of four, and the alternative is trusting a cookie nothing has
    /// checked.
    /// </remarks>
    public static bool HasOutlivedItsLimit(DateTimeOffset? signedInAt, DateTimeOffset now) =>
        signedInAt is not { } began || now - began >= AbsoluteLifetime;

    /// <summary>Whether the account is due another look.</summary>
    public static bool IsDueACheck(DateTimeOffset? checkedAt, DateTimeOffset now) =>
        checkedAt is not { } last || now - last >= RecheckEvery;

    /// <summary>The cookie handler's hook into all of the above.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.HttpContext.RequestServices;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        if (HasOutlivedItsLimit(Stamp(context, SignedInAt), now))
        {
            await EndAsync(context);
            return;
        }

        if (!IsDueACheck(Stamp(context, CheckedAt), now))
        {
            return;
        }

        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(id, out var staffId))
        {
            await EndAsync(context);
            return;
        }

        var auth = services.GetRequiredService<StaffAuthService>();
        var role = context.Principal?.FindFirstValue(ClaimTypes.Role);

        if (!await auth.IsSessionStillGoodAsync(staffId, role, context.HttpContext.RequestAborted))
        {
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(nameof(StaffSessionValidator))
                .LogInformation(
                    "Session for staff {StaffId} ended: the account is gone, inactive, or no longer {Role}",
                    staffId, role);

            await EndAsync(context);
            return;
        }

        // Checked and good. Writing the time back costs a cookie rewrite every couple of
        // minutes and saves a query on every request in between.
        context.Properties.Items[CheckedAt] = now.ToString("O");
        context.ShouldRenew = true;
    }

    /// <summary>Stamps a session at sign-in, so its ceiling and first check are known.</summary>
    public static void Begin(AuthenticationProperties properties, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(properties);

        properties.Items[SignedInAt] = now.ToString("O");
        properties.Items[CheckedAt] = now.ToString("O");
    }

    private static DateTimeOffset? Stamp(CookieValidatePrincipalContext context, string key) =>
        context.Properties.Items.TryGetValue(key, out var value)
        && DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var stamp)
            ? stamp
            : null;

    private static async Task EndAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();

        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
