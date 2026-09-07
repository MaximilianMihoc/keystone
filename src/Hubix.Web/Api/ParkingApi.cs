using System.Security.Claims;
using Hubix.Domain;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Hubix.Web.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Api;

/// <summary>
/// The car park, for staff.
/// </summary>
/// <remarks>
/// Spaces are entered here rather than imported: Hubix's sheets hold a parking price per
/// apartment and never enumerate the bays, so there is nothing to import from yet. Adding them
/// a range at a time is how a real car park is described — "A1 to A40, underground, level -1" —
/// rather than one form per space.
/// <para>
/// The writes live in <see cref="ParkingAdminService"/>. They used to sit in these endpoints,
/// against the context directly and without an audit row.
/// </para>
/// </remarks>
public static class ParkingApi
{
    public static void MapParking(this IEndpointRouteBuilder api)
    {
        api.MapGet("/projects/{projectId:guid}/parking",
            async (Guid projectId, HubixDbContext db, CancellationToken ct) =>
            {
                var project = await db.Projects
                    .AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => new { p.MaxParkingSpacesPerReservation })
                    .FirstOrDefaultAsync(ct);

                if (project is null)
                {
                    return Results.NotFound();
                }

                var prices = await db.ParkingPrices
                    .AsNoTracking()
                    .Where(p => p.ProjectId == projectId)
                    .Select(p => new
                    {
                        kind = p.Kind.ToString(),
                        priceNet = p.PriceNet,
                        depositNet = p.DepositNet
                    })
                    .ToListAsync(ct);

                var spaces = await db.ParkingSpaces
                    .AsNoTracking()
                    .Where(s => s.ProjectId == projectId)
                    .OrderBy(s => s.Code)
                    .Select(s => new
                    {
                        id = s.Id,
                        code = s.Code,
                        kind = s.Kind.ToString(),
                        level = s.Level,
                        state = s.State.ToString(),
                        adminNotes = s.AdminNotes,
                        heldForUnit = s.Reservation != null
                            ? s.Reservation.Unit!.Code
                            : s.Hold != null ? s.Hold.Unit!.Code : null
                    })
                    .ToListAsync(ct);

                return Results.Ok(new
                {
                    maxPerReservation = project.MaxParkingSpacesPerReservation,
                    prices,
                    spaces
                });
            });

        var editable = api.MapGroup("").RequireAuthorization(AuthPolicies.CanEdit);

        editable.MapPost("/projects/{projectId:guid}/parking",
            async (Guid projectId,
                   AddSpacesRequest request,
                   ParkingAdminService parking,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (!TryKind(request.Kind, out var kind, out var problem)
                    || !TryRange(request.Prefix, request.From, request.To, out var range, ref problem))
                {
                    return Results.BadRequest(new { error = problem });
                }

                return ProjectApi.Respond(await parking.AddSpacesAsync(
                    projectId, range, kind, request.Level,
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapPatch("/parking/{spaceId:guid}",
            async (Guid spaceId,
                   ParkingSpaceEdit edit,
                   ParkingAdminService parking,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                ParkingSpaceKind? kind = null;

                if (edit.Kind is { Length: > 0 } rawKind)
                {
                    if (!TryKind(rawKind, out var parsed, out var problem))
                    {
                        return Results.BadRequest(new { error = problem });
                    }

                    kind = parsed;
                }

                if (edit.Code is { Length: > 32 })
                {
                    return Results.BadRequest(new { error = "That space code is too long." });
                }

                return ProjectApi.Respond(await parking.EditSpaceAsync(
                    spaceId,
                    new SpaceEdit(edit.Code, kind, edit.Level, edit.AdminNotes),
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapDelete("/parking/{spaceId:guid}",
            async (Guid spaceId,
                   ParkingAdminService parking,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
                ProjectApi.Respond(await parking.DeleteSpaceAsync(
                    spaceId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)));

        editable.MapDelete("/projects/{projectId:guid}/parking",
            async (Guid projectId,
                   string prefix,
                   int from,
                   int to,
                   ParkingAdminService parking,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                string? problem = null;

                if (!TryRange(prefix, from, to, out var range, ref problem))
                {
                    return Results.BadRequest(new { error = problem });
                }

                return ProjectApi.Respond(await parking.RemoveRangeAsync(
                    projectId, range, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapPut("/projects/{projectId:guid}/parking-prices",
            async (Guid projectId,
                   ParkingPriceRequest request,
                   ParkingAdminService parking,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (!TryKind(request.Kind, out var kind, out var problem))
                {
                    return Results.BadRequest(new { error = problem });
                }

                if (request.PriceNet < 0 || request.DepositNet < 0)
                {
                    return Results.BadRequest(new { error = "A price cannot be negative." });
                }

                if (request.DepositNet > request.PriceNet)
                {
                    return Results.BadRequest(new
                    {
                        error = "The deposit cannot be more than the price it is part of."
                    });
                }

                var result = await parking.SetPriceAsync(
                    projectId, kind, request.PriceNet, request.DepositNet,
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return Saved(result);
            });

        editable.MapPut("/projects/{projectId:guid}/parking-max",
            async (Guid projectId,
                   MaxParkingRequest request,
                   ParkingAdminService parking,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (request.Max is < 0 or > 10)
                {
                    return Results.BadRequest(new { error = "Allow between 0 and 10 spaces." });
                }

                return Saved(await parking.SetMaxPerReservationAsync(
                    projectId, request.Max, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });
    }

    /// <summary>
    /// These two answered 204 before the writes moved into a service, and the client reads them
    /// that way. A success is a success whether or not anything actually differed.
    /// </summary>
    private static IResult Saved(AdminWriteResult result) => result switch
    {
        AdminWriteResult.Updated or AdminWriteResult.NoChange => Results.NoContent(),
        _ => ProjectApi.Respond(result)
    };

    private static bool TryKind(string? raw, out ParkingSpaceKind kind, out string? problem)
    {
        problem = null;

        if (Enum.TryParse(raw, out kind) && Enum.IsDefined(kind))
        {
            return true;
        }

        problem = $"\"{raw}\" is not a kind of space.";
        return false;
    }

    private static bool TryRange(string? prefix, int from, int to, out SpaceRange range, ref string? problem)
    {
        range = null!;

        if (from < 0 || to < from)
        {
            problem = "That is not a range of space numbers.";
            return false;
        }

        // Widened to long deliberately: as int this subtraction overflows, and a wide enough
        // range passes the check and then loops for hours.
        if ((long)to - from >= ParkingAdminService.MostAtOnce)
        {
            problem = $"That is more than {ParkingAdminService.MostAtOnce} spaces at once.";
            return false;
        }

        if (prefix is { Length: > 16 })
        {
            problem = "That prefix is too long.";
            return false;
        }

        range = new SpaceRange(prefix ?? "", from, to);
        return true;
    }

    public sealed record AddSpacesRequest(string Prefix, int From, int To, string Kind, int Level);

    public sealed record ParkingSpaceEdit(string? Code, string? Kind, int? Level, string? AdminNotes);

    public sealed record ParkingPriceRequest(string Kind, decimal PriceNet, decimal DepositNet);

    public sealed record MaxParkingRequest(int Max);
}
