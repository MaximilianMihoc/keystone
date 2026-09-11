using System.Security.Claims;
using System.Text.Json.Serialization;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Sales;
using Hubix.Web.Notifications;
using Hubix.Web.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Api;

/// <summary>
/// The sales pipeline, for the admin board.
/// </summary>
/// <remarks>
/// The stage list is served rather than duplicated in the client. The pipeline was inferred
/// from Hubix's documents and will need correcting; if the board hardcoded its own columns,
/// that correction would be two edits in two languages that could disagree.
/// </remarks>
public static class ReservationApi
{
    public static void MapReservations(this RouteGroupBuilder api)
    {
        api.MapGet("/pipeline", () => Results.Ok(new
        {
            stages = ReservationPipeline.Stages.Select(s => new
            {
                state = s.State.ToString(),
                label = s.Label,
                sourceColumn = s.SourceColumn,
                unitState = s.Unit.ToString(),
                recordsDate = s.RecordsDate,
                dateField = s.DateField is null ? null : char.ToLowerInvariant(s.DateField[0]) + s.DateField[1..]
            }),
            cancelled = nameof(ReservationState.Cancelled)
        }));

        api.MapGet("/projects/{projectId:guid}/reservations",
            async (Guid projectId, HubixDbContext db, CancellationToken ct) =>
            {
                var project = await db.Projects
                    .AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => new { id = p.Id, name = p.Name, reservationFee = p.ReservationFee })
                    .FirstOrDefaultAsync(ct);

                if (project is null)
                {
                    return Results.NotFound();
                }

                var reservations = await db.Reservations
                    .AsNoTracking()
                    .Where(r => r.Unit!.ProjectId == projectId)
                    .OrderBy(r => r.StateChangedAt)
                    .Select(r => new ReservationRow(
                        r.Id,
                        r.UnitId,
                        r.Unit!.Code,
                        r.Unit.UnitType!.Name,
                        r.Unit.FloorLevel,
                        r.State.ToString(),
                        r.StateChangedAt,
                        r.Prospect!.Email,
                        r.Prospect.FullName,
                        r.BuyerType.ToString(),
                        r.ContractNumber,
                        r.NotaryLocation,
                        r.NotaryAppointmentAt,
                        r.FeePaidAt,
                        r.AgreementSentAt,
                        r.SignedAgreementAt,
                        r.CesiuneSignedAt,
                        r.CompletedAt,
                        r.CancelledAt,
                        r.CancellationReason,
                        r.InternalNotes,
                        r.SnapReservationFee,
                        r.SnapParkingNet,
                        r.SnapParkingDepositNet,
                        r.ParkingSpaces
                            .OrderBy(s => s.Code)
                            .Select(s => new ReservationParking(
                                s.Id, s.Code, s.Kind.ToString(), s.Level, s.SnapPriceNet))
                            .ToList(),
                        r.SnapTranches))
                    .ToListAsync(ct);

                // What a reservation could be started on: a unit somebody is holding, and a
                // unit the inventory already calls reserved but which has no record behind it.
                // The second is how a project migrated from a spreadsheet catches up.
                var candidates = await db.Units
                    .AsNoTracking()
                    .Where(u => u.ProjectId == projectId
                                && (u.State == UnitState.Held || u.State == UnitState.Reserved)
                                && !u.Reservations.Any(r => r.State != ReservationState.Cancelled))
                    .OrderBy(u => u.FloorLevel).ThenBy(u => u.Code)
                    .Select(u => new
                    {
                        id = u.Id,
                        code = u.Code,
                        state = u.State.ToString(),
                        heldBy = u.Holds
                            .Where(h => h.ReleasedAt == null)
                            .Select(h => h.Prospect!.Email)
                            .FirstOrDefault()
                    })
                    .ToListAsync(ct);

                return Results.Ok(new { project, reservations, candidates });
            });

        // The waiting list, which today lives in Google Forms nobody reads until somebody
        // remembers to. Seeing the queue is most of the value of having one.
        api.MapGet("/projects/{projectId:guid}/waitlist",
            async (Guid projectId, HubixDbContext db, CancellationToken ct) =>
            {
                var entries = await db.WaitlistEntries
                    .AsNoTracking()
                    .Where(w => w.ProjectId == projectId && w.RemovedAt == null)
                    .OrderBy(w => w.UnitTypeId == null)
                    .ThenBy(w => w.Position)
                    .ThenBy(w => w.CreatedAt)
                    .Select(w => new
                    {
                        id = w.Id,
                        typeId = w.UnitTypeId,
                        typeName = w.UnitType == null ? null : w.UnitType.Name,
                        email = w.Prospect!.Email,
                        name = w.Prospect.FullName,
                        phone = w.Prospect.Phone,
                        joinedAt = w.CreatedAt,
                        notifiedAt = w.NotifiedAt
                    })
                    .ToListAsync(ct);

                return Results.Ok(entries);
            });

        var editable = api.MapGroup("").RequireAuthorization(AuthPolicies.CanEdit);

        editable.MapDelete("/waitlist/{entryId:guid}",
            async (Guid entryId,
                   WaitlistService waitlist,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                // People ask to come off by phone as often as by clicking a link.
                var removed = await waitlist.RemoveAsync(
                    entryId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return removed ? Results.Ok(new { removed = true }) : Results.NotFound();
            });

        editable.MapPost("/units/{unitId:guid}/reservation",
            async (Guid unitId,
                   StartRequest request,
                   ReservationService reservations,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                // A buyer is only needed when no hold already names one.
                var details = string.IsNullOrWhiteSpace(request.Email)
                    ? null
                    : new ProspectDetails(
                        request.Email,
                        request.FirstName,
                        request.Surname,
                        request.Phone,
                        request.City,
                        request.Country,
                        Cultures.Romanian,
                        ConsentGiven: false,
                        ConsentSource: "staff");

                var result = await reservations.CreateAsync(
                    unitId,
                    details,
                    request.BuyerType ?? BuyerType.Individual,
                    AdminApi.ActorId(user),
                    AdminApi.ActorLabel(user),
                    ct);

                return result switch
                {
                    ReservationOutcome.Created created => Results.Ok(new
                    {
                        id = created.ReservationId,
                        state = created.State.ToString()
                    }),
                    ReservationOutcome.NotFound => Results.NotFound(),
                    ReservationOutcome.Refused refused => Results.BadRequest(new { error = refused.Reason }),
                    _ => Results.Problem()
                };
            });

        editable.MapPost("/reservations/{reservationId:guid}/move",
            async (Guid reservationId,
                   MoveRequest request,
                   ReservationService reservations,
                   WaitlistPromoter promoter,
                   ILoggerFactory loggers,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (!Enum.TryParse<ReservationState>(request.State, out var target))
                {
                    return Results.BadRequest(new { error = $"{request.State} is not a stage." });
                }

                var result = await reservations.MoveAsync(
                    reservationId, target, request.Reason,
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                // A cancellation puts the apartment back on sale, and somebody may have been
                // waiting months for exactly that. The move is already committed, so a mail
                // failure must not report the cancellation as failed.
                if (result is ReservationOutcome.Moved { UnitState: UnitState.Available } moved)
                {
                    try
                    {
                        await promoter.PromoteAsync(moved.UnitId, ct);
                    }
                    catch (Exception ex)
                    {
                        loggers.CreateLogger(nameof(ReservationApi)).LogError(ex,
                            "Could not tell the waiting list that a unit came free");
                    }
                }

                return Render(result);
            });

        editable.MapPatch("/reservations/{reservationId:guid}",
            async (Guid reservationId,
                   ReservationEdit edit,
                   ReservationService reservations,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                var result = await reservations.EditAsync(
                    reservationId, edit, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return Render(result);
            });

        // Buyers change their minds about parking after reserving, in both directions. Before
        // this the only way to act on that was to cancel the sale and start again, losing its
        // place in the pipeline and its agreed price.
        editable.MapPost("/reservations/{reservationId:guid}/parking/{spaceId:guid}",
            async (Guid reservationId,
                   Guid spaceId,
                   ReservationService reservations,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                var result = await reservations.AddParkingAsync(
                    reservationId, spaceId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return Render(result);
            });

        editable.MapDelete("/reservations/{reservationId:guid}/parking/{spaceId:guid}",
            async (Guid reservationId,
                   Guid spaceId,
                   ReservationService reservations,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                var result = await reservations.RemoveParkingAsync(
                    reservationId, spaceId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return Render(result);
            });

        // What sales can offer this buyer right now: free bays on their project, priced.
        api.MapGet("/reservations/{reservationId:guid}/parking-available",
            async (Guid reservationId, HubixDbContext db, CancellationToken ct) =>
            {
                var projectId = await db.Reservations
                    .Where(r => r.Id == reservationId)
                    .Select(r => (Guid?)r.Unit!.ProjectId)
                    .FirstOrDefaultAsync(ct);

                if (projectId is null)
                {
                    return Results.NotFound();
                }

                var spaces = await db.ParkingSpaces
                    .AsNoTracking()
                    .Where(s => s.ProjectId == projectId && s.State == UnitState.Available)
                    .OrderBy(s => s.Level).ThenBy(s => s.Code)
                    .Select(s => new
                    {
                        id = s.Id,
                        code = s.Code,
                        kind = s.Kind.ToString(),
                        level = s.Level,

                        // Today's price: that is what they would be agreeing to.
                        priceNet = db.ParkingPrices
                            .Where(p => p.ProjectId == s.ProjectId && p.Kind == s.Kind)
                            .Select(p => (decimal?)p.PriceNet)
                            .FirstOrDefault()
                    })
                    .ToListAsync(ct);

                // A bay with no price for its kind cannot be sold, so it is not offered.
                return Results.Ok(spaces.Where(s => s.priceNet is not null).ToList());
            });
    }

    private static IResult Render(ReservationOutcome outcome) => outcome switch
    {
        ReservationOutcome.Moved moved => Results.Ok(new
        {
            state = moved.To.ToString(),
            unitState = moved.UnitState.ToString()
        }),
        ReservationOutcome.Updated updated => Results.Ok(new { changed = updated.Changes }),
        ReservationOutcome.NoChange => Results.Ok(new { changed = new Dictionary<string, object?>() }),
        ReservationOutcome.NotFound => Results.NotFound(),
        ReservationOutcome.Refused refused => Results.BadRequest(new { error = refused.Reason }),
        _ => Results.Problem()
    };

    /// <summary>
    /// One row of the board.
    /// </summary>
    /// <remarks>
    /// A named record rather than an anonymous type because of <see cref="TotalGross"/>: the
    /// agreed schedule is a JSON column, so summing it cannot be translated to SQL and has to
    /// happen once the row is in memory.
    /// </remarks>
    public sealed record ReservationRow(
        Guid Id,
        Guid UnitId,
        string UnitCode,
        string UnitType,
        int? FloorLevel,
        string State,
        DateTimeOffset StateChangedAt,
        string BuyerEmail,
        string? BuyerName,
        string BuyerType,
        string? ContractNumber,
        string? NotaryLocation,
        DateTimeOffset? NotaryAppointmentAt,
        DateTimeOffset? FeePaidAt,
        DateTimeOffset? AgreementSentAt,
        DateTimeOffset? SignedAgreementAt,
        DateTimeOffset? CesiuneSignedAt,
        DateTimeOffset? CompletedAt,
        DateTimeOffset? CancelledAt,
        string? CancellationReason,
        string? InternalNotes,
        decimal? ReservationFee,
        decimal ParkingNet,
        decimal ParkingDepositNet,
        IReadOnlyList<ReservationParking> Parking,
        [property: JsonIgnore] IReadOnlyList<TrancheAmount> Tranches)
    {
        public decimal TotalGross => Tranches.Sum(t => t.Gross);
    }

    /// <summary>One space on a sale, with what was agreed for it.</summary>
    public sealed record ReservationParking(
        Guid Id,
        string Code,
        string Kind,
        int Level,
        decimal? PriceNet);

    /// <summary>Starting a reservation. Everything is optional when a hold already names the buyer.</summary>
    public sealed record StartRequest(
        string? Email,
        string? FirstName,
        string? Surname,
        string? Phone,
        string? City,
        string? Country,
        BuyerType? BuyerType);

    public sealed record MoveRequest(string State, string? Reason);
}
