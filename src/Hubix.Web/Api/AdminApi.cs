using System.Globalization;
using System.Security.Claims;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Hubix.Web.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Api;

/// <summary>
/// JSON endpoints for the React admin client.
/// </summary>
/// <remarks>
/// Small on purpose. The public site is server-rendered and needs no API at all; this exists
/// only because the units grid and the pipeline board are genuinely app-like.
/// </remarks>
public static class AdminApi
{
    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/admin")
            .RequireAuthorization(AuthPolicies.Staff);

        api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            name = user.FindFirstValue(ClaimTypes.Name),
            email = user.FindFirstValue(ClaimTypes.Email),
            role = user.FindFirstValue(ClaimTypes.Role),
            canEdit = user.IsInRole(nameof(StaffRole.Admin)) || user.IsInRole(nameof(StaffRole.Sales))
        }));

        api.MapGet("/projects", async (HubixDbContext db, CancellationToken ct) =>
        {
            var culture = CultureInfo.CurrentUICulture.Name;

            var rows = await db.Projects
                .AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new
                {
                    p.Id,
                    p.Slug,
                    p.Name,
                    p.City,
                    p.Status,
                    p.UnitsCommitted,
                    Total = p.Units.Count,
                    Available = p.Units.Count(u => u.State == UnitState.Available),
                    Held = p.Units.Count(u => u.State == UnitState.Held),
                    Reserved = p.Units.Count(u => u.State == UnitState.Reserved),
                    Sold = p.Units.Count(u => u.State == UnitState.Sold ||
                                              u.State == UnitState.Contracted),
                    Cities = p.Translations
                        .Select(t => new { t.Culture, t.City })
                        .ToList()
                })
                .ToListAsync(ct);

            var projects = rows.Select(p => new
            {
                id = p.Id,
                slug = p.Slug,
                name = p.Name,
                city = p.Cities
                    .Select(t => new ProjectTranslation { Culture = t.Culture, City = t.City })
                    .CityFor(culture, p.City),
                status = p.Status.ToString(),
                unitsCommitted = p.UnitsCommitted,
                total = p.Total,
                available = p.Available,
                held = p.Held,
                reserved = p.Reserved,
                sold = p.Sold
            });

            return Results.Ok(projects);
        });

        api.MapGet("/projects/{projectId:guid}/units",
            async (Guid projectId, HubixDbContext db, CancellationToken ct) =>
            {
                var project = await db.Projects
                    .AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => new { p.Id, p.Name, p.Slug })
                    .FirstOrDefaultAsync(ct);

                if (project is null)
                {
                    return Results.NotFound();
                }

                var types = await db.UnitTypes
                    .AsNoTracking()
                    .Where(t => t.ProjectId == projectId)
                    .OrderBy(t => t.SortOrder)
                    .Select(t => new { id = t.Id, name = t.Name })
                    .ToListAsync(ct);

                var units = await db.Units
                    .AsNoTracking()
                    .Where(u => u.ProjectId == projectId)
                    .OrderBy(u => u.FloorLevel).ThenBy(u => u.Code)
                    .Select(u => new
                    {
                        id = u.Id,
                        crt = u.Crt,
                        code = u.Code,
                        building = u.Building,
                        stair = u.Stair,
                        floorLevel = u.FloorLevel,
                        unitTypeId = u.UnitTypeId,
                        orientation = u.Orientation,
                        areaBuilt = u.AreaBuilt,
                        areaUsable = u.AreaUsable,
                        areaBalcony = u.AreaBalcony,
                        areaTotal = u.AreaTotal,
                        basePriceNet = u.BasePriceNet,
                        cesiunePriceNet = u.CesiunePriceNet,
                        reducedVatEligible = u.ReducedVatEligible,
                        state = u.State.ToString(),
                        adminNotes = u.AdminNotes,

                        // Who is holding it and until when, so the grid can show why a unit
                        // is off the market without a second request.
                        holdExpiresAt = u.Holds
                            .Where(h => h.ReleasedAt == null)
                            .Select(h => (DateTimeOffset?)h.ExpiresAt)
                            .FirstOrDefault(),
                        heldBy = u.Holds
                            .Where(h => h.ReleasedAt == null)
                            .Select(h => h.Prospect!.Email)
                            .FirstOrDefault()
                    })
                    .ToListAsync(ct);

                return Results.Ok(new { project, types, units });
            });

        // Editing is a separate policy from reading. A viewer can see the grid; only admin
        // and sales may change what is in it.
        var editable = api.MapGroup("").RequireAuthorization(AuthPolicies.CanEdit);

        editable.MapPatch("/units/{unitId:guid}",
            async (Guid unitId,
                   UnitEdit edit,
                   UnitAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                var result = await admin.EditAsync(unitId, edit, ActorId(user), ActorLabel(user), ct);

                return result switch
                {
                    UnitEditResult.Updated updated => Results.Ok(new { changed = updated.Changes }),
                    UnitEditResult.NoChange => Results.Ok(new { changed = new Dictionary<string, object?>() }),
                    UnitEditResult.NotFound => Results.NotFound(),
                    UnitEditResult.Rejected rejected => Results.BadRequest(new { error = rejected.Reason }),
                    _ => Results.Problem()
                };
            });

        editable.MapPost("/units/{unitId:guid}/withdrawn",
            async (Guid unitId,
                   WithdrawRequest request,
                   UnitAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                var result = await admin.SetWithdrawnAsync(
                    unitId, request.Withdrawn, ActorId(user), ActorLabel(user), ct);

                return result switch
                {
                    UnitEditResult.Updated updated => Results.Ok(new { changed = updated.Changes }),
                    UnitEditResult.NoChange => Results.Ok(new { changed = new Dictionary<string, object?>() }),
                    UnitEditResult.NotFound => Results.NotFound(),
                    UnitEditResult.Rejected rejected => Results.BadRequest(new { error = rejected.Reason }),
                    _ => Results.Problem()
                };
            });

        api.MapParking();
        api.MapReservations();
        api.MapImport();
        api.MapAssets();
        api.MapPrivacy();

        api.MapGet("/audit", async (
            HubixDbContext db,
            Guid? entityId,
            int take,
            CancellationToken ct) =>
        {
            var query = db.AuditLogs.AsNoTracking();

            if (entityId is { } id)
            {
                query = query.Where(a => a.EntityId == id);
            }

            var entries = await query
                .OrderByDescending(a => a.CreatedAt)
                .Take(Math.Clamp(take == 0 ? 50 : take, 1, 200))
                .Select(a => new
                {
                    at = a.CreatedAt,
                    actor = a.ActorLabel ?? a.ActorType,
                    action = a.Action,
                    entityType = a.EntityType,
                    entityId = a.EntityId,
                    before = a.Before,
                    after = a.After
                })
                .ToListAsync(ct);

            return Results.Ok(entries);
        });
    }

    internal static Guid ActorId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    internal static string ActorLabel(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email) ?? "unknown";

    public sealed record WithdrawRequest(bool Withdrawn);
}
