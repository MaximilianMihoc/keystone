using Keystone.Infrastructure.Admin;
using Keystone.Web.Pages.Admin;

namespace Keystone.Web.Api;

/// <summary>
/// The log of who did what, and when.
/// </summary>
/// <remarks>
/// Admin only. Every row names a person — colleagues included — so reading the whole log is a
/// supervisory view rather than part of doing the job, which puts it with staff accounts and
/// privacy rather than with the inventory screens.
/// </remarks>
public static class AuditApi
{
    public static void MapAudit(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/audit").RequireAuthorization(AuthPolicies.Admin);

        admin.MapGet("", async (
            Guid? entityId,
            string? entityType,
            string? action,
            string? actorType,
            string? actor,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? take,
            AuditService audit,
            CancellationToken ct) =>
            Results.Ok(await audit.SearchAsync(
                new AuditFilter(entityId, entityType, action, actorType, actor, from, to, take),
                ct)));

        // What the log actually holds, so the filters offer what is there rather than what the
        // code could in principle write.
        admin.MapGet("/kinds", async (AuditService audit, CancellationToken ct) =>
            Results.Ok(await audit.KindsAsync(ct)));
    }
}
