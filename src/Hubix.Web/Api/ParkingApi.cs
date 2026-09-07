using System.Globalization;
using System.Security.Claims;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
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
                    .OrderBy(p => p.Kind)
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
                    .OrderBy(s => s.Level).ThenBy(s => s.Code)
                    .Select(s => new
                    {
                        id = s.Id,
                        code = s.Code,
                        kind = s.Kind.ToString(),
                        level = s.Level,
                        state = s.State.ToString(),
                        adminNotes = s.AdminNotes,

                        // Which apartment took it, through whichever of the two links is live.
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
                   HubixDbContext db,
                   CancellationToken ct) =>
            {
                if (!Enum.TryParse<ParkingSpaceKind>(request.Kind, out var kind)
                    || !Enum.IsDefined(kind))
                {
                    return Results.BadRequest(new { error = $"\"{request.Kind}\" is not a kind of space." });
                }

                if (request.From < 0 || request.To < request.From)
                {
                    return Results.BadRequest(new { error = "That is not a range of space numbers." });
                }

                // A whole car park at once is fine; a typo asking for a hundred thousand bays
                // is not. Widened to long deliberately: as int this subtraction overflows and
                // a wide enough range passes the check, then loops for hours.
                if ((long)request.To - request.From >= 500)
                {
                    return Results.BadRequest(new { error = "That is more than 500 spaces at once." });
                }

                if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
                {
                    return Results.NotFound();
                }

                var existing = await db.ParkingSpaces
                    .Where(s => s.ProjectId == projectId)
                    .Select(s => s.Code)
                    .ToListAsync(ct);

                var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                var skipped = new List<string>();
                var created = 0;

                // Padded to the width of the largest number in the range, so A1–A10 becomes
                // A01–A10. Codes sort as text everywhere — in the grid, in the allocation
                // order, in the email naming the buyer's spaces — and unpadded numbering puts
                // A10 before A2. Car park signage numbers this way for the same reason.
                var width = request.To.ToString(CultureInfo.InvariantCulture).Length;

                for (var i = request.From; i <= request.To; i++)
                {
                    var code = request.Prefix.Trim()
                        + i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

                    // Codes already in use are reported rather than failing the whole range:
                    // extending a car park from A20 to A40 should not be refused because A20
                    // exists.
                    if (!taken.Add(code))
                    {
                        skipped.Add(code);
                        continue;
                    }

                    db.ParkingSpaces.Add(new ParkingSpace
                    {
                        ProjectId = projectId,
                        Code = code,
                        Kind = kind,
                        Level = request.Level
                    });

                    created++;
                }

                await db.SaveChangesAsync(ct);

                return Results.Ok(new { created, skipped });
            });

        editable.MapPatch("/parking/{spaceId:guid}",
            async (Guid spaceId,
                   ParkingSpaceEdit edit,
                   HubixDbContext db,
                   CancellationToken ct) =>
            {
                var space = await db.ParkingSpaces
                    .Include(s => s.Hold!).ThenInclude(h => h.Unit)
                    .Include(s => s.Reservation!).ThenInclude(r => r.Unit)
                    .FirstOrDefaultAsync(s => s.Id == spaceId, ct);

                if (space is null)
                {
                    return Results.NotFound();
                }

                // A space someone is holding or has bought is not free to be renumbered, moved
                // to another floor, or reclassified underneath them. The kind matters most:
                // the price a buyer is charged is looked up from it at conversion, so changing
                // it changes what they pay for the bay they were quoted. Only the note is free.
                var describes = edit.Code is not null || edit.Kind is not null || edit.Level is not null;

                if (space.State is not UnitState.Available && describes)
                {
                    return Results.BadRequest(new { error = InUse(space, "changed") });
                }

                if (edit.Kind is { Length: > 0 } rawKind)
                {
                    if (!Enum.TryParse<ParkingSpaceKind>(rawKind, out var kind) || !Enum.IsDefined(kind))
                    {
                        return Results.BadRequest(new { error = $"\"{rawKind}\" is not a kind of space." });
                    }

                    space.Kind = kind;
                }

                if (edit.Code is { Length: > 0 } code)
                {
                    space.Code = code.Trim();
                }

                if (edit.Level is { } level)
                {
                    space.Level = level;
                }

                if (edit.AdminNotes is not null)
                {
                    space.AdminNotes = edit.AdminNotes;
                }

                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // UX_ParkingSpaces_ProjectCode. Two bays cannot share a number.
                    return Results.BadRequest(new
                    {
                        error = $"Another space here is already called {space.Code}."
                    });
                }

                return Results.Ok(new { changed = new { space.Code, Kind = space.Kind.ToString(), space.Level } });
            });

        editable.MapDelete("/parking/{spaceId:guid}",
            async (Guid spaceId, HubixDbContext db, CancellationToken ct) =>
            {
                var space = await db.ParkingSpaces
                    .Include(s => s.Hold!).ThenInclude(h => h.Unit)
                    .Include(s => s.Reservation!).ThenInclude(r => r.Unit)
                    .FirstOrDefaultAsync(s => s.Id == spaceId, ct);

                if (space is null)
                {
                    return Results.NotFound();
                }

                if (space.State is not UnitState.Available)
                {
                    return Results.BadRequest(new { error = InUse(space, "removed") });
                }

                db.ParkingSpaces.Remove(space);
                await db.SaveChangesAsync(ct);

                return Results.NoContent();
            });

        editable.MapDelete("/projects/{projectId:guid}/parking",
            async (Guid projectId,
                   string prefix,
                   int from,
                   int to,
                   HubixDbContext db,
                   CancellationToken ct) =>
            {
                if (from < 0 || to < from || (long)to - from >= 500)
                {
                    return Results.BadRequest(new { error = "That is not a range of space numbers." });
                }

                // Matched by code rather than by recomputing the padding: a range may have been
                // added at one width and extended at another, and the codes are what exist.
                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var i = from; i <= to; i++)
                {
                    var n = i.ToString(CultureInfo.InvariantCulture);
                    wanted.Add(prefix.Trim() + n);
                    wanted.Add(prefix.Trim() + n.PadLeft(2, '0'));
                    wanted.Add(prefix.Trim() + n.PadLeft(3, '0'));
                }

                var spaces = await db.ParkingSpaces
                    .Include(s => s.Hold!).ThenInclude(h => h.Unit)
                    .Include(s => s.Reservation!).ThenInclude(r => r.Unit)
                    .Where(s => s.ProjectId == projectId && wanted.Contains(s.Code))
                    .ToListAsync(ct);

                // A range is a blunt instrument, so the ones somebody is using are kept and
                // named rather than the whole request being refused because of one.
                var kept = spaces.Where(s => s.State is not UnitState.Available).ToList();
                var removable = spaces.Where(s => s.State is UnitState.Available).ToList();

                db.ParkingSpaces.RemoveRange(removable);
                await db.SaveChangesAsync(ct);

                return Results.Ok(new
                {
                    removed = removable.Count,
                    kept = kept.Select(s => s.Code).Order(StringComparer.Ordinal).ToList()
                });
            });

        editable.MapPut("/projects/{projectId:guid}/parking-prices",
            async (Guid projectId,
                   ParkingPriceRequest request,
                   HubixDbContext db,
                   CancellationToken ct) =>
            {
                if (!Enum.TryParse<ParkingSpaceKind>(request.Kind, out var kind) || !Enum.IsDefined(kind))
                {
                    return Results.BadRequest(new { error = $"\"{request.Kind}\" is not a kind of space." });
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

                // Checked here rather than left to the foreign key, which would surface as a
                // 500 for what is simply an unknown project.
                if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
                {
                    return Results.NotFound();
                }

                var price = await db.ParkingPrices
                    .FirstOrDefaultAsync(p => p.ProjectId == projectId && p.Kind == kind, ct);

                if (price is null)
                {
                    price = new ParkingPrice { ProjectId = projectId, Kind = kind };
                    db.ParkingPrices.Add(price);
                }

                // Reservations already agreed keep their own snapshot, so repricing here never
                // moves what somebody has signed for.
                price.PriceNet = request.PriceNet;
                price.DepositNet = request.DepositNet;

                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            });

        editable.MapPut("/projects/{projectId:guid}/parking-max",
            async (Guid projectId,
                   MaxParkingRequest request,
                   HubixDbContext db,
                   CancellationToken ct) =>
            {
                if (request.Max is < 0 or > 10)
                {
                    return Results.BadRequest(new { error = "Allow between 0 and 10 spaces." });
                }

                var rows = await db.Projects
                    .Where(p => p.Id == projectId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.MaxParkingSpacesPerReservation, request.Max), ct);

                return rows == 0 ? Results.NotFound() : Results.NoContent();
            });
    }

    /// <summary>
    /// Why a space cannot be touched, naming the apartment it went with. "It is held" leaves an
    /// admin hunting; "it is held with Ap.105" tells them what to release.
    /// </summary>
    private static string InUse(ParkingSpace space, string verb)
    {
        var unit = space.Reservation?.Unit?.Code ?? space.Hold?.Unit?.Code;
        var state = space.State.ToString().ToLowerInvariant();

        return unit is null
            ? $"{space.Code} is {state} and cannot be {verb}."
            : $"{space.Code} is {state} with {unit} and cannot be {verb}. Free it first.";
    }

    public sealed record AddSpacesRequest(string Prefix, int From, int To, string Kind, int Level);

    public sealed record ParkingSpaceEdit(string? Code, string? Kind, int? Level, string? AdminNotes);

    public sealed record ParkingPriceRequest(string Kind, decimal PriceNet, decimal DepositNet);

    public sealed record MaxParkingRequest(int Max);
}
