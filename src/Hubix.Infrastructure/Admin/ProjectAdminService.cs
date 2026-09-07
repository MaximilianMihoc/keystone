using System.Globalization;
using System.Text;
using System.Text.Json;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Admin;

/// <summary>
/// The fields a developer record carries.
/// </summary>
public sealed record DeveloperEdit(
    string Name,
    string? Description,
    int? YearsActive,
    int? UnitsBuilt,
    string? Website);

/// <summary>
/// What it takes to start a project: a name, a city, and optionally the address it will live at.
/// </summary>
/// <remarks>
/// Deliberately four fields rather than the whole record. A project has fourteen editable
/// columns, and asking for all of them before the row exists turns creation into a wizard —
/// which is the shape this admin was explicitly not built as. Everything else defaults from
/// the entity and is filled in afterwards, on the setup screen.
/// </remarks>
public sealed record ProjectCreate(
    string Name,
    string City,
    string? Slug,
    Guid? DeveloperId);

/// <summary>
/// Everything the project setup form owns.
/// </summary>
/// <remarks>
/// Unlike <see cref="UnitEdit"/>, a null here means null, not "not sent". The form loads the
/// whole record and saves it behind one button, so clearing a delivery date or detaching a
/// developer has to be expressible — and under PATCH semantics it would not be.
/// <para>
/// Absent on purpose: <c>PublishedAt</c>, which has its own endpoint because publishing is an
/// act rather than a field; and <c>MaxParkingSpacesPerReservation</c>, which is already
/// written by the parking screen. One writer per column.
/// </para>
/// </remarks>
public sealed record ProjectEdit(
    string Name,
    string City,
    string Slug,
    Guid? DeveloperId,
    ProjectStatus Status,
    int? UnitsCommitted,
    DateOnly? DeliveryDate,
    int DeliveryGraceMonths,
    RentalProfile? RentalProfile,
    decimal? ReservationFee,
    string Currency,
    int HoldDurationHours,
    string? MapsUrl);

/// <summary>
/// The translatable prose of a project, for one culture.
/// </summary>
public sealed record TranslationEdit(
    string? City,
    string? Summary,
    string? Body,
    string? PenaltyClause,
    string? Parking);

/// <summary>
/// What a configuration write did.
/// </summary>
/// <remarks>
/// One union across projects, developers and translations rather than one per table. The
/// endpoints map it to HTTP identically in every case, and three near-identical unions would
/// differ only in their name.
/// </remarks>
public abstract record AdminWriteResult
{
    public sealed record Created(Guid Id) : AdminWriteResult;

    public sealed record Updated(IReadOnlyDictionary<string, object?> Changes) : AdminWriteResult;

    public sealed record NoChange : AdminWriteResult;

    public sealed record Deleted : AdminWriteResult;

    public sealed record NotFound : AdminWriteResult;

    public sealed record Rejected(string Reason) : AdminWriteResult;
}

/// <summary>
/// Creating and editing the things a project is made of, so that onboarding one no longer
/// means editing the seeder and redeploying.
/// </summary>
/// <remarks>
/// Projects, their prose and their developers share a class because they share a screen and a
/// lifetime. A developer has five scalar columns and exists to be attached to a project; giving
/// it its own service would duplicate the audit, result and duplicate-key plumbing for four
/// one-line methods. The day a developer grows children of its own, the method names here are
/// already the seam to split along.
/// <para>
/// None of these writes opens an explicit transaction. Each is one entity plus its audit row in
/// a single <c>SaveChangesAsync</c>, which is already atomic and already replayable. The
/// execution-strategy wrapper elsewhere in this assembly exists for writes that span several
/// statements; copying it here would be ceremony.
/// </para>
/// </remarks>
public sealed class ProjectAdminService(
    HubixDbContext db,
    TimeProvider clock,
    ILogger<ProjectAdminService> logger)
{
    // Publishing takes a project from Draft to Upcoming. Anything further along its life has
    // already passed that point, so republishing must not drag it backwards.
    private const ProjectStatus FirstPublicStatus = ProjectStatus.Upcoming;

    /// <summary>
    /// Turns a project name into the address it will be reached at.
    /// </summary>
    /// <remarks>
    /// Diacritic folding is the part that matters and the part that is easy to leave out:
    /// "București" has to become "bucuresti", or the URL carries percent-escapes that nobody
    /// can read back to a project. Decomposing to <c>FormD</c> splits a letter from its accent
    /// so the accent can be dropped on its own.
    /// </remarks>
    public static string Slugify(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingHyphen = false;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(ch));
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        // The column is nvarchar(120); truncating can strand a trailing hyphen, so trim after.
        return builder.ToString() is var slug && slug.Length > 120
            ? slug[..120].TrimEnd('-')
            : slug;
    }

    public async Task<AdminWriteResult> CreateDeveloperAsync(
        DeveloperEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        if (await db.Developers.AnyAsync(d => d.Name == edit.Name, ct))
        {
            return new AdminWriteResult.Rejected($"There is already a developer called {edit.Name}.");
        }

        var developer = new Developer
        {
            Name = edit.Name,
            Description = edit.Description,
            YearsActive = edit.YearsActive,
            UnitsBuilt = edit.UnitsBuilt,
            Website = edit.Website
        };

        db.Developers.Add(developer);
        Audit(actorId, actorLabel, "developer", developer.Id, "developer.created", null, edit);

        return await SaveAsync(
            ct,
            () => new AdminWriteResult.Created(developer.Id),
            () => DuplicateNameAsync(edit.Name, null, ct));
    }

    public async Task<AdminWriteResult> EditDeveloperAsync(
        Guid developerId,
        DeveloperEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var developer = await db.Developers.FirstOrDefaultAsync(d => d.Id == developerId, ct);

        if (developer is null)
        {
            return new AdminWriteResult.NotFound();
        }

        if (developer.Name != edit.Name
            && await db.Developers.AnyAsync(d => d.Name == edit.Name && d.Id != developerId, ct))
        {
            return new AdminWriteResult.Rejected($"There is already a developer called {edit.Name}.");
        }

        var diff = new Diff();

        diff.Set("Name", developer.Name, edit.Name, v => developer.Name = v);
        diff.Set("Description", developer.Description, edit.Description, v => developer.Description = v);
        diff.Set("YearsActive", developer.YearsActive, edit.YearsActive, v => developer.YearsActive = v);
        diff.Set("UnitsBuilt", developer.UnitsBuilt, edit.UnitsBuilt, v => developer.UnitsBuilt = v);
        diff.Set("Website", developer.Website, edit.Website, v => developer.Website = v);

        if (diff.IsEmpty)
        {
            return new AdminWriteResult.NoChange();
        }

        Audit(actorId, actorLabel, "developer", developer.Id, "developer.edited", diff.Before, diff.Changes);

        return await SaveAsync(
            ct,
            () => new AdminWriteResult.Updated(diff.Changes),
            () => DuplicateNameAsync(edit.Name, developerId, ct));
    }

    /// <summary>
    /// Removes a developer nobody is using.
    /// </summary>
    /// <remarks>
    /// This guard is the only protection there is. The foreign key is <c>SetNull</c>, so the
    /// database would happily strip the developer from a live project and leave the public
    /// home page missing a name, without complaining once.
    /// </remarks>
    public async Task<AdminWriteResult> DeleteDeveloperAsync(
        Guid developerId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var developer = await db.Developers.FirstOrDefaultAsync(d => d.Id == developerId, ct);

        if (developer is null)
        {
            return new AdminWriteResult.NotFound();
        }

        var projects = await db.Projects.CountAsync(p => p.DeveloperId == developerId, ct);

        if (projects > 0)
        {
            return new AdminWriteResult.Rejected(
                $"{developer.Name} is on {Count(projects, "project")}, so it cannot be removed. " +
                "Detach it there first.");
        }

        db.Developers.Remove(developer);
        Audit(actorId, actorLabel, "developer", developerId, "developer.deleted", new { developer.Name }, null);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("{Actor} deleted developer {DeveloperId}", actorLabel, developerId);

        return new AdminWriteResult.Deleted();
    }

    public async Task<AdminWriteResult> CreateProjectAsync(
        ProjectCreate create,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var slug = string.IsNullOrWhiteSpace(create.Slug) ? Slugify(create.Name) : Slugify(create.Slug);

        if (slug.Length == 0)
        {
            return new AdminWriteResult.Rejected("That name does not make a web address; type one instead.");
        }

        if (await db.Projects.AnyAsync(p => p.Slug == slug, ct))
        {
            return new AdminWriteResult.Rejected(TakenSlug(slug));
        }

        if (create.DeveloperId is { } developerId
            && !await db.Developers.AnyAsync(d => d.Id == developerId, ct))
        {
            return new AdminWriteResult.Rejected("That developer no longer exists.");
        }

        var project = new Project
        {
            Slug = slug,
            Name = create.Name,
            City = create.City,
            DeveloperId = create.DeveloperId
        };

        db.Projects.Add(project);
        Audit(actorId, actorLabel, "project", project.Id, "project.created", null, new { project.Slug, project.Name, project.City });

        return await SaveAsync(
            ct,
            () => new AdminWriteResult.Created(project.Id),
            () => TakenSlugAsync(slug, null, ct));
    }

    public async Task<AdminWriteResult> EditProjectAsync(
        Guid projectId,
        ProjectEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return new AdminWriteResult.NotFound();
        }

        var slug = Slugify(edit.Slug);

        if (slug.Length == 0)
        {
            return new AdminWriteResult.Rejected("That is not a web address a project can live at.");
        }

        // The entity has promised this since the schema was written, and nothing has enforced
        // it. Six public pages resolve a project by its slug, so moving one while it is live
        // breaks every link anybody has shared.
        if (slug != project.Slug && project.PublishedAt is not null)
        {
            return new AdminWriteResult.Rejected(
                $"{project.Name} is published, so its web address cannot change. Unpublish it first.");
        }

        if (slug != project.Slug && await db.Projects.AnyAsync(p => p.Slug == slug && p.Id != projectId, ct))
        {
            return new AdminWriteResult.Rejected(TakenSlug(slug));
        }

        if (edit.DeveloperId is { } developerId
            && !await db.Developers.AnyAsync(d => d.Id == developerId, ct))
        {
            return new AdminWriteResult.Rejected("That developer no longer exists.");
        }

        var diff = new Diff();

        diff.Set("Slug", project.Slug, slug, v => project.Slug = v);
        diff.Set("Name", project.Name, edit.Name, v => project.Name = v);
        diff.Set("City", project.City, edit.City, v => project.City = v);
        diff.Set("DeveloperId", project.DeveloperId, edit.DeveloperId, v => project.DeveloperId = v);
        diff.Set("Status", project.Status, edit.Status, v => project.Status = v);
        diff.Set("UnitsCommitted", project.UnitsCommitted, edit.UnitsCommitted, v => project.UnitsCommitted = v);
        diff.Set("DeliveryDate", project.DeliveryDate, edit.DeliveryDate, v => project.DeliveryDate = v);
        diff.Set("DeliveryGraceMonths", project.DeliveryGraceMonths, edit.DeliveryGraceMonths, v => project.DeliveryGraceMonths = v);
        diff.Set("RentalProfile", project.RentalProfile, edit.RentalProfile, v => project.RentalProfile = v);
        diff.Set("ReservationFee", project.ReservationFee, edit.ReservationFee, v => project.ReservationFee = v);
        diff.Set("Currency", project.Currency, edit.Currency, v => project.Currency = v);
        diff.Set("HoldDurationHours", project.HoldDurationHours, edit.HoldDurationHours, v => project.HoldDurationHours = v);
        diff.Set("MapsUrl", project.MapsUrl, edit.MapsUrl, v => project.MapsUrl = v);

        if (diff.IsEmpty)
        {
            return new AdminWriteResult.NoChange();
        }

        Audit(actorId, actorLabel, "project", project.Id, "project.edited", diff.Before, diff.Changes);

        return await SaveAsync(
            ct,
            () => new AdminWriteResult.Updated(diff.Changes),
            () => TakenSlugAsync(slug, projectId, ct));
    }

    /// <summary>
    /// Makes a project visible to the public, or takes it back out of view.
    /// </summary>
    /// <remarks>
    /// Two fields describe different things and are both kept honest here.
    /// <c>PublishedAt</c> is visibility: every public query gates on it. <c>Status</c> is where
    /// the project is in its selling life. Publishing therefore advances a project that is
    /// still <c>Draft</c>, because a visible draft is a contradiction, but leaves anything
    /// further along alone.
    /// <para>
    /// A project with no apartments is refused. Everything the public site renders about a
    /// project is derived from its units, so publishing an empty one produces a page
    /// advertising "0 free of 0" — one rule here is cheaper and more honest than an empty
    /// state on every public page.
    /// </para>
    /// </remarks>
    public async Task<AdminWriteResult> SetPublishedAsync(
        Guid projectId,
        bool published,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return new AdminWriteResult.NotFound();
        }

        if (project.IsPublished == published)
        {
            return new AdminWriteResult.NoChange();
        }

        if (published && !await db.Units.AnyAsync(u => u.ProjectId == projectId, ct))
        {
            return new AdminWriteResult.Rejected(
                $"{project.Name} has no apartments yet, so there would be nothing to show. " +
                "Add apartments before publishing it.");
        }

        var diff = new Diff();

        DateTimeOffset? publishedAt = published ? clock.GetUtcNow() : null;

        diff.Set("PublishedAt", project.PublishedAt, publishedAt, v => project.PublishedAt = v);

        if (published && project.Status == ProjectStatus.Draft)
        {
            diff.Set("Status", project.Status, FirstPublicStatus, v => project.Status = v);
        }

        Audit(
            actorId,
            actorLabel,
            "project",
            project.Id,
            published ? "project.published" : "project.unpublished",
            diff.Before,
            diff.Changes);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Actor} {Action} project {ProjectId}",
            actorLabel, published ? "published" : "unpublished", project.Id);

        return new AdminWriteResult.Updated(diff.Changes);
    }

    /// <summary>
    /// Writes the prose for one language, creating the row if this is the first time.
    /// </summary>
    public async Task<AdminWriteResult> SaveTranslationAsync(
        Guid projectId,
        string culture,
        TranslationEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            return new AdminWriteResult.NotFound();
        }

        var translation = await db.ProjectTranslations
            .FirstOrDefaultAsync(t => t.ProjectId == projectId && t.Culture == culture, ct);

        var diff = new Diff();

        if (translation is null)
        {
            translation = new ProjectTranslation { ProjectId = projectId, Culture = culture };
            db.ProjectTranslations.Add(translation);
        }

        diff.Set("City", translation.City, edit.City, v => translation.City = v);
        diff.Set("Summary", translation.Summary, edit.Summary, v => translation.Summary = v);
        diff.Set("Body", translation.Body, edit.Body, v => translation.Body = v);
        diff.Set("PenaltyClause", translation.PenaltyClause, edit.PenaltyClause, v => translation.PenaltyClause = v);
        diff.Set("Parking", translation.Parking, edit.Parking, v => translation.Parking = v);

        if (diff.IsEmpty && db.Entry(translation).State != EntityState.Added)
        {
            return new AdminWriteResult.NoChange();
        }

        // Audited against the project rather than the translation row: the audit screen filters
        // on one entity id, and prose edits belong in the trail of the project they describe.
        Audit(
            actorId,
            actorLabel,
            "project",
            projectId,
            "translation.saved",
            diff.Before,
            new { Culture = culture, Fields = diff.Changes });

        return await SaveAsync(
            ct,
            () => new AdminWriteResult.Updated(diff.Changes),
            async () => await db.ProjectTranslations
                .AnyAsync(t => t.ProjectId == projectId && t.Culture == culture, ct)
                ? "That language was saved by someone else a moment ago. Reload and try again."
                : null);
    }

    /// <summary>
    /// Removes a project that never got off the ground.
    /// </summary>
    /// <remarks>
    /// Only an empty, unpublished project. Deleting a populated one is a different and far more
    /// destructive feature: holds cascade from units, so a project with no reservations can
    /// still have live ones destroyed silently; assets cascade too, while the only code that
    /// removes the underlying file is the asset service, so the files would be orphaned in
    /// storage forever. The need this actually serves is "I mistyped the name, start again",
    /// and that project is empty.
    /// </remarks>
    public async Task<AdminWriteResult> DeleteProjectAsync(
        Guid projectId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return new AdminWriteResult.NotFound();
        }

        // ponytail: read-then-delete. An import racing a delete could slip through; a guarded
        // delete is the upgrade if two admins ever collide.
        var blockers = await db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new
            {
                Units = p.Units.Count,
                Parking = p.ParkingSpaces.Count,
                Files = p.Assets.Count,
                Reservations = p.Units.SelectMany(u => u.Reservations).Count(),
                Holds = p.Units.SelectMany(u => u.Holds).Count(),

                // WaitlistEntry points at Project without a collection navigation on the other
                // side, so this one cannot be reached through p.
                Waiting = db.WaitlistEntries.Count(w => w.ProjectId == projectId)
            })
            .FirstAsync(ct);

        if (project.IsPublished)
        {
            return new AdminWriteResult.Rejected(
                $"{project.Name} is published. Unpublish it before deleting it.");
        }

        List<string> parts =
        [
            .. Part(blockers.Units, "apartment"),
            .. Part(blockers.Parking, "parking space"),
            .. Part(blockers.Files, "file"),
            .. Part(blockers.Reservations, "reservation"),
            .. Part(blockers.Holds, "hold"),
            .. Part(blockers.Waiting, "person on the waiting list", "people on the waiting list")
        ];

        if (parts.Count > 0)
        {
            return new AdminWriteResult.Rejected(
                $"{project.Name} still has {Join(parts)}. Remove them before deleting it.");
        }

        // Removed through the change tracker rather than ExecuteDelete, so that the row and its
        // audit entry go in one SaveChanges. ExecuteDelete runs on its own the moment it is
        // called, which would leave a deleted project with no record of who deleted it if the
        // audit write then failed.
        db.Projects.Remove(project);
        Audit(actorId, actorLabel, "project", projectId, "project.deleted", new { project.Name, project.Slug }, null);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Actor} deleted project {ProjectId}", actorLabel, projectId);

        return new AdminWriteResult.Deleted();
    }

    /// <summary>
    /// Saves, turning a lost race for a unique column into a sentence rather than a 500.
    /// </summary>
    /// <remarks>
    /// The caller has already checked for a duplicate, and that check can still lose to another
    /// admin between the query and the write. <paramref name="explain"/> re-asks the database
    /// what went wrong instead of assuming: a <c>DbUpdateException</c> is not automatically a
    /// duplicate, and reporting a disk-full as "that name is taken" would send somebody looking
    /// in the wrong place.
    /// </remarks>
    private async Task<AdminWriteResult> SaveAsync(
        CancellationToken ct,
        Func<AdminWriteResult> success,
        Func<Task<string?>> explain)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            var reason = await explain();

            if (reason is null)
            {
                throw;
            }

            return new AdminWriteResult.Rejected(reason);
        }

        return success();
    }

    private async Task<string?> TakenSlugAsync(string slug, Guid? exceptId, CancellationToken ct) =>
        await db.Projects.AnyAsync(p => p.Slug == slug && (exceptId == null || p.Id != exceptId), ct)
            ? TakenSlug(slug)
            : null;

    private async Task<string?> DuplicateNameAsync(string name, Guid? exceptId, CancellationToken ct) =>
        await db.Developers.AnyAsync(d => d.Name == name && (exceptId == null || d.Id != exceptId), ct)
            ? $"There is already a developer called {name}."
            : null;

    private static string TakenSlug(string slug) =>
        $"Another project already uses the address /projects/{slug}. Choose another.";

    private void Audit(
        Guid actorId,
        string actorLabel,
        string entityType,
        Guid entityId,
        string action,
        object? before,
        object? after) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = after is null ? null : JsonSerializer.Serialize(after)
        });

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static IEnumerable<string> Part(int n, string singular, string? plural = null) =>
        n == 0 ? [] : [n == 1 ? $"{n} {singular}" : $"{n} {plural ?? singular + "s"}"];

    private static string Join(List<string> parts) =>
        parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts[..^1]) + " and " + parts[^1];

    /// <summary>
    /// Collects what a save changed, so the audit row and the response say the same thing.
    /// </summary>
    private sealed class Diff
    {
        public Dictionary<string, object?> Before { get; } = [];

        public Dictionary<string, object?> Changes { get; } = [];

        public bool IsEmpty => Changes.Count == 0;

        public void Set<T>(string name, T current, T incoming, Action<T> set)
        {
            if (EqualityComparer<T>.Default.Equals(current, incoming))
            {
                return;
            }

            Before[name] = current;
            Changes[name] = incoming;
            set(incoming);
        }
    }
}
