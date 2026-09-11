using System.Security.Claims;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
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
                        salesInbox = p.SalesInbox,
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
                        parking = row?.Parking,
                        reservationNotice = row?.ReservationNotice
                    };
                });

                return Results.Ok(new { project, developers, translations });
            });

        // The payment plan in effect, anything scheduled to replace it, and a real unit to preview
        // against. The sample is the cheapest, because a schedule that looks right on the cheapest
        // apartment looks right on all of them, and rounding shows up soonest at the bottom.
        api.MapGet("/projects/{projectId:guid}/pricing",
            async (Guid projectId, HubixDbContext db, TimeProvider clock, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
                {
                    return Results.NotFound();
                }

                var now = clock.GetUtcNow();

                var rules = await db.PricingRules
                    .AsNoTracking()
                    .Where(r => r.ProjectId == projectId)
                    .OrderByDescending(r => r.EffectiveFrom)
                    .ToListAsync(ct);

                var sample = await db.Units
                    .AsNoTracking()
                    .Where(u => u.ProjectId == projectId)
                    .OrderBy(u => u.BasePriceNet)
                    .Select(u => new
                    {
                        code = u.Code,
                        basePriceNet = u.BasePriceNet,
                        cesiuneNet = u.CesiunePriceNet ?? 0m,
                        reducedVatEligible = u.ReducedVatEligible
                    })
                    .FirstOrDefaultAsync(ct);

                return Results.Ok(new
                {
                    current = rules.Where(r => r.EffectiveFrom <= now).Select(Shape).FirstOrDefault(),
                    scheduled = rules.Where(r => r.EffectiveFrom > now).Select(Shape).Reverse(),
                    sample
                });
            });

        // Writes nothing: it exists so the editor can show what a plan would produce before
        // anybody commits to it. A rule with a mistake in it comes back as problems rather than
        // as an exception from the calculator.
        api.MapPost("/projects/{projectId:guid}/pricing/preview",
            async (Guid projectId,
                   PreviewRequest request,
                   HubixDbContext db,
                   CancellationToken ct) =>
            {
                if (!TryReadTranches(request.Rule, out var edit, out var problem))
                {
                    return Results.BadRequest(new { error = problem });
                }

                var problems = PricingRuleValidator.Validate(
                    edit.AdvancePct, edit.VatStandard, edit.CesiuneVatPct, edit.VatReduced, edit.Tranches);

                if (problems.Count > 0)
                {
                    return Results.Ok(new { problems, schedule = (object?)null });
                }

                var sample = await db.Units
                    .AsNoTracking()
                    .Where(u => u.ProjectId == projectId)
                    .OrderBy(u => u.BasePriceNet)
                    .Select(u => new { u.Code, u.BasePriceNet, Cesiune = u.CesiunePriceNet ?? 0m })
                    .FirstOrDefaultAsync(ct);

                // A price sent by the editor wins over the sample apartment. The preview is a
                // what-if, and trying the plan against a different number is the point of it —
                // the cheapest apartment is only where the number starts. With nothing sent the
                // sample answers, and a project with no apartments at all falls back to a round
                // number, so the screen is usable before an import has ever run.
                var basePrice = request.SamplePriceNet ?? sample?.BasePriceNet ?? 100_000m;
                var cesiune = request.SampleCesiuneNet ?? sample?.Cesiune ?? 0m;

                if (cesiune > basePrice)
                {
                    return Results.BadRequest(new { error = "The cesiune cannot exceed the price it is part of." });
                }

                var rule = new PricingRule
                {
                    ProjectId = projectId,
                    AdvancePct = edit.AdvancePct,
                    VatStandard = edit.VatStandard,
                    CesiuneVatPct = edit.CesiuneVatPct,
                    VatReduced = edit.VatReduced,
                    Tranches = edit.Tranches
                };

                var breakdown = TrancheCalculator.Calculate(rule, basePrice, cesiune, request.ReducedVat);

                return Results.Ok(new
                {
                    problems = Array.Empty<string>(),
                    schedule = new
                    {
                        against = sample?.Code,
                        basePriceNet = breakdown.BasePriceNet,
                        cesiuneNet = breakdown.CesiuneNet,
                        developerPriceNet = breakdown.DeveloperPriceNet,
                        vatRate = breakdown.VatRate,
                        totalNet = breakdown.TotalNet,
                        totalVat = breakdown.TotalVat,
                        totalGross = breakdown.TotalGross,
                        tranches = breakdown.Tranches.Select(t => new
                        {
                            label = t.Label,
                            due = t.Due.ToString(),
                            percentage = t.Percentage,
                            apartmentNet = t.ApartmentNet,
                            cesiuneNet = t.CesiuneNet,
                            net = t.Net,
                            vat = t.Vat,
                            gross = t.Gross
                        })
                    }
                });
            });

        // The catalogue belongs to the portal, not to a project, so it is read without one.
        // Its counts are portal-wide for the same reason: they gate the delete button, and
        // DeleteUnitTypeAsync refuses on those same numbers.
        api.MapGet("/unit-types",
            async (HubixDbContext db, CancellationToken ct) =>
            {
                var types = await db.UnitTypes
                    .AsNoTracking()
                    .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
                    .Select(t => new
                    {
                        id = t.Id,
                        name = t.Name,
                        sortOrder = t.SortOrder,
                        units = t.Units.Count,
                        waiting = db.WaitlistEntries.Count(w => w.UnitTypeId == t.Id),
                        displayNames = t.Translations
                            .Select(x => new { x.Culture, x.DisplayName })
                            .ToList()
                    })
                    .ToListAsync(ct);

                return Results.Ok(new { types, cultures = Cultures.Offered });
            });

        api.MapGet("/projects/{projectId:guid}/structure",
            async (Guid projectId, HubixDbContext db, CancellationToken ct) =>
            {
                if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
                {
                    return Results.NotFound();
                }

                var floors = await db.Floors
                    .AsNoTracking()
                    .Where(f => f.ProjectId == projectId)
                    .OrderBy(f => f.Building).ThenBy(f => f.Level)
                    .Select(f => new
                    {
                        id = f.Id,
                        building = f.Building,
                        level = f.Level,
                        label = f.Label,
                        hasPlan = f.FloorPlanAssetId != null
                    })
                    .ToListAsync(ct);

                // Floors join their apartments by building and level rather than by key, so the
                // screen shows which levels the apartments actually claim. A floor row nobody
                // lives on, or a level with no floor row, is worth seeing.
                var occupied = await db.Units
                    .AsNoTracking()
                    .Where(u => u.ProjectId == projectId && u.FloorLevel != null)
                    .GroupBy(u => new { u.Building, u.FloorLevel })
                    .Select(g => new { building = g.Key.Building, level = g.Key.FloorLevel!.Value, units = g.Count() })
                    .ToListAsync(ct);

                return Results.Ok(new
                {
                    floors,
                    occupied = occupied.OrderBy(o => o.building).ThenBy(o => o.level)
                });
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
                    ?? Length(request.Parking, "parking note", 400)
                    ?? Length(request.ReservationNotice, "reservation notice", 2000);

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
                        Blank(request.Parking),
                        Blank(request.ReservationNotice)),
                    AdminApi.ActorId(user),
                    AdminApi.ActorLabel(user),
                    ct);

                return Respond(result);
            });

        editable.MapPut("/projects/{projectId:guid}/pricing",
            async (Guid projectId,
                   PricingRequest request,
                   PricingAdminService pricing,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (!TryReadTranches(request, out var edit, out var problem))
                {
                    return Results.BadRequest(new { error = problem });
                }

                return Respond(await pricing.SaveCurrentAsync(
                    projectId, edit, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapPost("/projects/{projectId:guid}/pricing/scheduled",
            async (Guid projectId,
                   ScheduleRequest request,
                   PricingAdminService pricing,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (!TryReadTranches(request.Rule, out var edit, out var problem))
                {
                    return Results.BadRequest(new { error = problem });
                }

                return Respond(await pricing.ScheduleAsync(
                    projectId, edit, request.EffectiveFrom,
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapDelete("/pricing/{ruleId:guid}",
            async (Guid ruleId,
                   PricingAdminService pricing,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
                Respond(await pricing.DeleteScheduledAsync(
                    ruleId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)));

        editable.MapPost("/unit-types",
            async (UnitTypeCreateRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (Blank(request.Name) is not { } name)
                {
                    return Results.BadRequest(new { error = "An apartment type needs a name." });
                }

                if (Length(name, "type name", 100) is { } problem)
                {
                    return Results.BadRequest(new { error = problem });
                }

                return Respond(await admin.CreateUnitTypeAsync(
                    name, request.SortOrder,
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapPut("/unit-types/{unitTypeId:guid}",
            async (Guid unitTypeId,
                   UnitTypeRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                var names = request.DisplayNames ?? [];

                foreach (var (culture, name) in names)
                {
                    if (!Cultures.Offered.Contains(culture, StringComparer.OrdinalIgnoreCase))
                    {
                        return Results.BadRequest(new { error = $"\"{culture}\" is not a language this site offers." });
                    }

                    if (name is { Length: > 100 })
                    {
                        return Results.BadRequest(new { error = "That name is too long." });
                    }
                }

                return Respond(await admin.EditUnitTypeAsync(
                    unitTypeId,
                    new UnitTypeEdit(request.SortOrder, names),
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapDelete("/unit-types/{unitTypeId:guid}",
            async (Guid unitTypeId,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
                Respond(await admin.DeleteUnitTypeAsync(
                    unitTypeId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)));

        editable.MapPost("/projects/{projectId:guid}/floors",
            async (Guid projectId,
                   FloorRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (Floor(request) is { } problem)
                {
                    return Results.BadRequest(new { error = problem });
                }

                return Respond(await admin.CreateFloorAsync(
                    projectId, new FloorEdit(request.Building, request.Level, request.Label),
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapPut("/floors/{floorId:guid}",
            async (Guid floorId,
                   FloorRequest request,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (Floor(request) is { } problem)
                {
                    return Results.BadRequest(new { error = problem });
                }

                return Respond(await admin.EditFloorAsync(
                    floorId, new FloorEdit(request.Building, request.Level, request.Label),
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct));
            });

        editable.MapDelete("/floors/{floorId:guid}",
            async (Guid floorId,
                   ProjectAdminService admin,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
                Respond(await admin.DeleteFloorAsync(
                    floorId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)));
    }

    /// <summary>Levels run from deep basements to tall towers, but not to absurdity.</summary>
    private static string? Floor(FloorRequest request) =>
        request.Level is < -10 or > 200 ? "That is not a floor level."
        : Length(request.Building, "block name", 50) ?? Length(request.Label, "floor name", 100);

    /// <summary>
    /// Reads a payment plan off the wire, refusing anything that is not a plan at all.
    /// </summary>
    /// <remarks>
    /// Only shape is checked here — enum names that parse, and a tranche list that exists.
    /// Whether the plan makes sense is <see cref="PricingRuleValidator"/>'s job, and it says so
    /// in sentences the editor puts next to the fields.
    /// </remarks>
    private static bool TryReadTranches(PricingRequest request, out PricingRuleEdit edit, out string? problem)
    {
        edit = null!;
        problem = null;

        var tranches = new List<TrancheDefinition>();

        foreach (var t in request.Tranches ?? [])
        {
            if (!Enum.TryParse<TrancheBasis>(t.Basis, out var basis) || !Enum.IsDefined(basis))
            {
                problem = $"\"{t.Basis}\" is not a way of working out a payment.";
                return false;
            }

            if (!Enum.TryParse<TrancheDue>(t.Due, out var due) || !Enum.IsDefined(due))
            {
                problem = $"\"{t.Due}\" is not a moment a payment falls due.";
                return false;
            }

            if (t.Label is { Length: > 100 })
            {
                problem = "That payment name is too long.";
                return false;
            }

            tranches.Add(new TrancheDefinition(t.Label?.Trim() ?? "", basis, t.IncludesCesiune, due, t.Percentage));
        }

        edit = new PricingRuleEdit(
            request.AdvancePct,
            request.VatStandard,
            request.CesiuneVatPct,
            request.VatReduced,
            tranches);

        return true;
    }

    private static object Shape(PricingRule rule) => new
    {
        id = rule.Id,
        effectiveFrom = rule.EffectiveFrom,
        advancePct = rule.AdvancePct,
        vatStandard = rule.VatStandard,
        cesiuneVatPct = rule.CesiuneVatPct,
        vatReduced = rule.VatReduced,
        tranches = rule.Tranches.Select(t => new
        {
            label = t.Label,
            basis = t.Basis.ToString(),
            includesCesiune = t.IncludesCesiune,
            due = t.Due.ToString(),
            percentage = t.Percentage
        })
    };

    /// <summary>
    /// Maps a write outcome onto HTTP, the same way every other admin endpoint does.
    /// </summary>
    internal static IResult Respond(AdminWriteResult result) => result switch
    {
        AdminWriteResult.Created created => Results.Ok(new { id = created.Id }),
        AdminWriteResult.Payload payload => Results.Ok(payload.Value),
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
            ?? Length(request.MapsUrl, "map link", 1000)
            ?? Length(request.SalesInbox, "sales inbox", 320);

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
            Blank(request.MapsUrl),
            Blank(request.SalesInbox));

        return true;
    }

    private static string? Check(DeveloperRequest request) =>
        Text(request.Name, "name", 200, "developer") ?? Length(request.Website, "website", 500);

    private static string? Text(string? value, string what, int max, string subject = "project") =>
        string.IsNullOrWhiteSpace(value) ? $"A {subject} needs a {what}."
        : value.Trim().Length > max ? $"That {what} is too long."
        : null;

    internal static string? Length(string? value, string what, int max) =>
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
        string? MapsUrl,
        string? SalesInbox);

    public sealed record TranslationRequest(
        string? City,
        string? Summary,
        string? Body,
        string? PenaltyClause,
        string? Parking,
        string? ReservationNotice);

    public sealed record PublishRequest(bool Published);

    public sealed record TrancheRequest(
        string? Label,
        string Basis,
        bool IncludesCesiune,
        string Due,
        decimal? Percentage);

    public sealed record PricingRequest(
        decimal AdvancePct,
        decimal VatStandard,
        decimal CesiuneVatPct,
        decimal? VatReduced,
        IReadOnlyList<TrancheRequest>? Tranches);

    /// <summary>
    /// A plan to try out. A sample figure sent here overrides the apartment the preview would
    /// otherwise use, and is what answers for a project with no apartments at all.
    /// </summary>
    public sealed record PreviewRequest(
        PricingRequest Rule,
        decimal? SamplePriceNet,
        decimal? SampleCesiuneNet,
        bool ReducedVat);

    public sealed record ScheduleRequest(PricingRequest Rule, DateTimeOffset EffectiveFrom);

    public sealed record UnitTypeCreateRequest(string? Name, int SortOrder);

    public sealed record UnitTypeRequest(int SortOrder, Dictionary<string, string>? DisplayNames);

    public sealed record FloorRequest(string? Building, int Level, string? Label);
}
