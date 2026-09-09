using System.Security.Claims;
using Hubix.Domain;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Hubix.Web.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Api;

/// <summary>
/// The people on the other side of the sale, and whether they may still reserve.
/// </summary>
/// <remarks>
/// Admin only, for the reason <see cref="PrivacyApi"/> gives: sales can change a price, but
/// deciding that somebody is a serial canceller — or that they are not, after all — is a
/// judgement about a person, and it should take a deliberate choice about who may make it.
/// </remarks>
public static class ProspectApi
{
    /// <summary>
    /// How many rows a list request returns unless it asks for fewer, and the ceiling.
    /// </summary>
    /// <remarks>
    /// This is the first table in the system that grows with every visitor rather than with
    /// the inventory, so it does not follow the other lists in fetching everything and
    /// filtering in the browser. Searching happens in SQL and the result is bounded.
    /// </remarks>
    private const int DefaultTake = 100;

    private const int MaxTake = 500;

    public static void MapProspects(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/prospects").RequireAuthorization(AuthPolicies.Admin);

        admin.MapGet("", async (
            string? search,
            bool? blocked,
            int? take,
            HubixDbContext db,
            CancellationToken ct) =>
        {
            var query = db.Prospects.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim();

                // No ToLower: SQL Server's default collation is case-insensitive, and calling
                // it here would only stop the email index being used.
                query = query.Where(p =>
                    p.Email.Contains(needle)
                    || (p.FullName != null && p.FullName.Contains(needle))
                    || (p.Phone != null && p.Phone.Contains(needle)));
            }

            if (blocked is { } wanted)
            {
                query = query.Where(p => p.CanHold != wanted);
            }

            var rows = await query
                .OrderByDescending(p => p.CreatedAt)
                .Take(Math.Clamp(take ?? DefaultTake, 1, MaxTake))
                .Select(p => new
                {
                    id = p.Id,
                    email = p.Email,
                    fullName = p.FullName,
                    phone = p.Phone,
                    source = p.Source,
                    firstSeen = p.CreatedAt,
                    canHold = p.CanHold,
                    blockReason = p.BlockReason,
                    lapsedHoldCount = p.LapsedHoldCount,

                    // Whether the block is currently biting, and whether lifting it matters.
                    activeHolds = p.Holds.Count(h => h.ReleasedAt == null),
                    openReservations = p.Reservations
                        .Count(r => r.State != ReservationState.Cancelled)
                })
                .ToListAsync(ct);

            return Results.Ok(rows);
        });

        // Everything held about one person, in a single query. Deliberately not
        // PrivacyService.ExportAsync: that is shaped as a subject access response, looks a
        // person up by email, and is serialised to a file somebody forwards. Bending it to
        // fit a drawer would tie the two together for no gain.
        admin.MapGet("/{prospectId:guid}", async (
            Guid prospectId,
            HubixDbContext db,
            CancellationToken ct) =>
        {
            var prospect = await db.Prospects
                .AsNoTracking()
                .Where(p => p.Id == prospectId)
                .Select(p => new
                {
                    id = p.Id,
                    email = p.Email,
                    fullName = p.FullName,
                    phone = p.Phone,
                    locale = p.Locale,
                    source = p.Source,
                    firstSeen = p.CreatedAt,
                    canHold = p.CanHold,
                    blockReason = p.BlockReason,
                    lapsedHoldCount = p.LapsedHoldCount,
                    activeHolds = p.Holds.Count(h => h.ReleasedAt == null),
                    openReservations = p.Reservations
                        .Count(r => r.State != ReservationState.Cancelled),

                    gdprConsentAt = p.GdprConsentAt,
                    gdprConsentPurpose = p.GdprConsentPurpose,
                    gdprConsentSource = p.GdprConsentSource,
                    deletionRequestedAt = p.DeletionRequestedAt,

                    holds = p.Holds
                        .OrderByDescending(h => h.CreatedAt)
                        .Select(h => new
                        {
                            id = h.Id,
                            unitCode = h.Unit!.Code,
                            project = h.Unit.Project!.Name,
                            takenAt = h.CreatedAt,
                            expiresAt = h.ExpiresAt,
                            releasedAt = h.ReleasedAt,
                            releaseReason = h.ReleaseReason == null
                                ? null
                                : h.ReleaseReason.ToString()
                        }),

                    reservations = p.Reservations
                        .OrderByDescending(r => r.CreatedAt)
                        .Select(r => new
                        {
                            id = r.Id,
                            unitCode = r.Unit!.Code,
                            project = r.Unit.Project!.Name,
                            state = r.State.ToString(),
                            startedAt = r.CreatedAt,
                            cancelledAt = r.CancelledAt,
                            cancellationReason = r.CancellationReason
                        }),

                    waitlist = p.WaitlistEntries
                        .OrderByDescending(w => w.CreatedAt)
                        .Select(w => new
                        {
                            id = w.Id,
                            project = w.Project!.Name,
                            typeName = w.UnitType == null ? null : w.UnitType.Name,
                            position = w.Position,
                            joinedAt = w.CreatedAt,
                            notifiedAt = w.NotifiedAt,
                            removedAt = w.RemovedAt
                        })
                })
                .FirstOrDefaultAsync(ct);

            return prospect is null ? Results.NotFound() : Results.Ok(prospect);
        });

        admin.MapPost("/{prospectId:guid}/blocked", async (
            Guid prospectId,
            BlockRequest request,
            ProspectAdminService prospects,
            ClaimsPrincipal user,
            CancellationToken ct) =>
            ProjectApi.Respond(await prospects.SetBlockedAsync(
                prospectId,
                request.Blocked,
                request.Reason,
                AdminApi.ActorId(user),
                AdminApi.ActorLabel(user),
                ct)));
    }

    public sealed record BlockRequest(bool Blocked, string? Reason);
}
