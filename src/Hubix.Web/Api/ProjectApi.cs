using System.Security.Claims;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Hubix.Web.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Api;

/// <summary>
/// Creating and configuring the projects themselves, for staff.
/// </summary>
/// <remarks>
/// Until this existed, a project, its developer, its prose and its pricing could only be
/// created by editing the seeder and redeploying — which meant onboarding a second project was
/// a code change. The importer has always handled inventory; this handles the configuration
/// the inventory hangs off.
/// </remarks>
public static class ProjectApi
{
    public static void MapProjects(this IEndpointRouteBuilder api)
    {
        api.MapGet("/developers", async (HubixDbContext db, CancellationToken ct) =>
        {
            var developers = await db.Developers
                .AsNoTracking()
                .OrderBy(d => d.Name)
                .Select(d => new
                {
                    id = d.Id,
                    name = d.Name,
                    description = d.Description,
                    yearsActive = d.YearsActive,
                    unitsBuilt = d.UnitsBuilt,
                    website = d.Website,

                    // Shown next to the delete button, so that "this cannot be removed" is
                    // visible before it is clicked rather than after.
                    projectCount = d.Projects.Count
                })
                .ToListAsync(ct);

            return Results.Ok(developers);
        });

        // Everything a project's setup screen needs, in one request. The canonical City is
        // returned rather than the culture-resolved one the projects list serves, because this
        // is the screen that edits it.
        api.MapGet("/projects/{projectId:guid}/setup",
            async (Guid projectId, HubixDbContext db, CancellationToken ct) =>
            {
                var project = await db.Projects
                    .AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => new
                    {
                        id = p.Id,
                        slug = p.Slug,
                        name = p.Name,
                        city = p.City,
                        developerId = p.DeveloperId,
                        status = p.Status.ToString(),
                        unitsCommitted = p.UnitsCommitted,
                        deliveryDate = p.DeliveryDate,
                        deliveryGraceMonths = p.DeliveryGraceMonths,
                        rentalProfile = p.RentalProfile == null ? null : p.RentalProfile.ToString(),
                        reservationFee = p.ReservationFee,
                        currency = p.Currency,
                        holdDurationHours = p.HoldDurationHours,
                        mapsUrl = p.MapsUrl,
                        publishedAt = p.PublishedAt,
                        units = p.Units.Count
                    })
                    .FirstOrDefaultAsync(ct);

                if (project is null)
                {
                    return Results.NotFound();
                }

                var developers = await db.Developers
                    .AsNoTracking()
                    .OrderBy(d => d.Name)
                    .Select(d => new { id = d.Id, name = d.Name })
                    .ToListAsync(ct);

                var stored = await db.ProjectTranslations
                    .AsNoTracking()
                    .Where(t => t.ProjectId == projectId)
                    .ToListAsync(ct);

                // Always one row per offered language, blank where none is stored, so the
                // client renders two identical blocks and never has to know that saving one
                // is sometimes an insert and sometimes an update.
                var translations = Cultures.Offered.Select(culture =>
                {
                    var row = stored.FirstOrDefault(t => t.Culture == culture);

                    return new
                    {
                        culture,
                        isSource = culture == Cultures.Source,
                        city = row?.City,
                        summary = row?.Summary,
                        body = row?.Body,
                        penaltyClause = row?.PenaltyClause,
                        parking = row?.Parking
                    };
                });

                return Results.Ok(new { project, developers, translations });
            });

        var editable = api.MapGroup("").RequireAuthorization(AuthPolicies.CanEdit);

        editable.MapPost("/developers",
            async (DeveloperRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (Check(request) is { } problem)
                {
                    return Results.BadRequest(new { error = problem });
                }

                var result = await admin.CreateDeveloperAsync(
                    request.ToEdit(), AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return Respond(result);
            });

        editable.MapPut("/developers/{developerId:guid}",
            async (Guid developerId,
                   DeveloperRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (Check(request) is { } problem)
                {
                    return Results.BadRequest(new { error = problem });
                }

                var result = await admin.EditDeveloperAsync(
                    developerId, request.ToEdit(), AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return Respond(result);
            });

        editable.MapDelete("/developers/{developerId:guid}",
            async (Guid developerId,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
                Respond(await admin.DeleteDeveloperAsync(
                    developerId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)));

        editable.MapPost("/projects",
            async (CreateProjectRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (Text(request.Name, "name", 200) is { } nameProblem)
                {
                    return Results.BadRequest(new { error = nameProblem });
                }

                if (Text(request.City, "city", 120) is { } cityProblem)
                {
                    return Results.BadRequest(new { error = cityProblem });
                }

                if (request.Slug is { Length: > 120 })
                {
                    return Results.BadRequest(new { error = "That web address is too long." });
                }

                var result = await admin.CreateProjectAsync(
                    new ProjectCreate(request.Name.Trim(), request.City.Trim(), request.Slug, request.DeveloperId),
                    AdminApi.ActorId(user),
                    AdminApi.ActorLabel(user),
                    ct);

                return Respond(result);
            });

        editable.MapPut("/projects/{projectId:guid}",
            async (Guid projectId,
                   ProjectRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (!TryRead(request, out var edit, out var problem))
                {
                    return Results.BadRequest(new { error = problem });
                }

                var result = await admin.EditProjectAsync(
                    projectId, edit, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return Respond(result);
            });

        editable.MapDelete("/projects/{projectId:guid}",
            async (Guid projectId,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
                Respond(await admin.DeleteProjectAsync(
                    projectId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)));

        // Mirrors POST /units/{id}/withdrawn: the same verb and the same one-flag body, because
        // publishing and withdrawing are the same kind of act on different things.
        editable.MapPost("/projects/{projectId:guid}/published",
            async (Guid projectId,
                   PublishRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
                Respond(await admin.SetPublishedAsync(
                    projectId, request.Published, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)));

        editable.MapPut("/projects/{projectId:guid}/translations/{culture}",
            async (Guid projectId,
                   string culture,
                   TranslationRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                // The Culture column has no database constraint, so this check is the only
                // thing between the model and a translation nothing will ever read.
                if (!Cultures.Offered.Contains(culture, StringComparer.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = $"\"{culture}\" is not a language this site offers." });
                }

                var problem = Length(request.City, "city", 120)
                    ?? Length(request.Summary, "summary", 2000)
                    ?? Length(request.PenaltyClause, "penalty clause", 2000)
                    ?? Length(request.Parking, "parking note", 400);

                if (problem is not null)
                {
                    return Results.BadRequest(new { error = problem });
                }

                var result = await admin.SaveTranslationAsync(
                    projectId,
                    Cultures.Offered.First(c => c.Equals(culture, StringComparison.OrdinalIgnoreCase)),
                    new TranslationEdit(
                        Blank(request.City),
                        Blank(request.Summary),
                        Blank(request.Body),
                        Blank(request.PenaltyClause),
                        Blank(request.Parking)),
                    AdminApi.ActorId(user),
                    AdminApi.ActorLabel(user),
                    ct);

                return Respond(result);
            });
    }

    /// <summary>
    /// Maps a write outcome onto HTTP, the same way every other admin endpoint does.
    /// </summary>
    private static IResult Respond(AdminWriteResult result) => result switch
    {
        AdminWriteResult.Created created => Results.Ok(new { id = created.Id }),
        AdminWriteResult.Updated updated => Results.Ok(new { changed = updated.Changes }),
        AdminWriteResult.NoChange => Results.Ok(new { changed = new Dictionary<string, object?>() }),
        AdminWriteResult.Deleted => Results.NoContent(),
        AdminWriteResult.NotFound => Results.NotFound(),
        AdminWriteResult.Rejected rejected => Results.BadRequest(new { error = rejected.Reason }),
        _ => Results.Problem()
    };

    /// <summary>
    /// Reads the project form, refusing anything the database would refuse less politely.
    /// </summary>
    /// <remarks>
    /// Every check here guards a constraint that already exists: the length limits guard column
    /// widths that would otherwise throw a truncation error, and the hold duration guards
    /// <c>CK_Projects_HoldDurationHours</c>, which without this surfaces as a 500 rather than
    /// as a sentence somebody can act on.
    /// </remarks>
    private static bool TryRead(ProjectRequest request, out ProjectEdit edit, out string? problem)
    {
        edit = null!;
        problem = Text(request.Name, "name", 200)
            ?? Text(request.City, "city", 120)
            ?? Text(request.Slug, "web address", 120)
            ?? Length(request.MapsUrl, "map link", 1000);

        if (problem is not null)
        {
            return false;
        }

        if (!Enum.TryParse<ProjectStatus>(request.Status, out var status) || !Enum.IsDefined(status))
        {
            problem = $"\"{request.Status}\" is not a project status.";
            return false;
        }

        RentalProfile? rentalProfile = null;

        if (!string.IsNullOrWhiteSpace(request.RentalProfile))
        {
            if (!Enum.TryParse<RentalProfile>(request.RentalProfile, out var parsed) || !Enum.IsDefined(parsed))
            {
                problem = $"\"{request.RentalProfile}\" is not a way of letting a home.";
                return false;
            }

            rentalProfile = parsed;
        }

        if (request.Currency is not { Length: 3 })
        {
            problem = "A currency is three letters, like EUR.";
            return false;
        }

        if (request.HoldDurationHours is < 1 or > 720)
        {
            problem = "A hold lasts between one hour and thirty days.";
            return false;
        }

        if (request.ReservationFee < 0 || request.UnitsCommitted < 0 || request.DeliveryGraceMonths < 0)
        {
            problem = "That cannot be a negative number.";
            return false;
        }

        edit = new ProjectEdit(
            request.Name.Trim(),
            request.City.Trim(),
            request.Slug.Trim(),
            request.DeveloperId,
            status,
            request.UnitsCommitted,
            request.DeliveryDate,
            request.DeliveryGraceMonths,
            rentalProfile,
            request.ReservationFee,
            request.Currency.ToUpperInvariant(),
            request.HoldDurationHours,
            Blank(request.MapsUrl));

        return true;
    }

    private static string? Check(DeveloperRequest request) =>
        Text(request.Name, "name", 200, "developer") ?? Length(request.Website, "website", 500);

    private static string? Text(string? value, string what, int max, string subject = "project") =>
        string.IsNullOrWhiteSpace(value) ? $"A {subject} needs a {what}."
        : value.Trim().Length > max ? $"That {what} is too long."
        : null;

    private static string? Length(string? value, string what, int max) =>
        value is not null && value.Length > max ? $"That {what} is too long." : null;

    /// <summary>An empty box means no value, not an empty string.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed record DeveloperRequest(
        string Name,
        string? Description,
        int? YearsActive,
        int? UnitsBuilt,
        string? Website)
    {
        public DeveloperEdit ToEdit() =>
            new(Name.Trim(), Blank(Description), YearsActive, UnitsBuilt, Blank(Website));
    }

    public sealed record CreateProjectRequest(string Name, string City, string? Slug, Guid? DeveloperId);

    public sealed record ProjectRequest(
        string Name,
        string City,
        string Slug,
        Guid? DeveloperId,
        string Status,
        int? UnitsCommitted,
        DateOnly? DeliveryDate,
        int DeliveryGraceMonths,
        string? RentalProfile,
        decimal? ReservationFee,
        string Currency,
        int HoldDurationHours,
        string? MapsUrl);

    public sealed record TranslationRequest(
        string? City,
        string? Summary,
        string? Body,
        string? PenaltyClause,
        string? Parking);

    public sealed record PublishRequest(bool Published);
}
