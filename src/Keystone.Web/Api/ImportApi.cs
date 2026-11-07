using System.Security.Claims;
using System.Text.Json;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Import;
using Keystone.Web.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Web.Api;

/// <summary>
/// Uploading an operator's inventory workbook, and getting one back out.
/// </summary>
/// <remarks>
/// Nothing here writes without a preview first. Upload returns what the file would do; commit
/// takes the same file again and applies it. The file is not stashed between the two: it is
/// re-read, and a hash taken at preview has to match, so "apply what I just read" is checked
/// rather than assumed.
/// </remarks>
public static class ImportApi
{
    /// <summary>These sheets run to forty rows. Anything far larger is not one of them.</summary>
    private const long MaxUploadBytes = 10 * 1024 * 1024;

    public static void MapImport(this RouteGroupBuilder api)
    {
        api.MapGet("/import/fields", () => Results.Ok(
            ImportField.All.Select(f => new
            {
                field = f.Field,
                label = f.Label,
                required = f.Required,
                aliases = f.Aliases
            })));

        api.MapGet("/projects/{projectId:guid}/import/profiles",
            async (Guid projectId, KeystoneDbContext db, CancellationToken ct) =>
            {
                var profiles = await db.ImportProfiles
                    .AsNoTracking()
                    .Where(p => p.ProjectId == projectId)
                    .OrderByDescending(p => p.LastUsedAt ?? p.CreatedAt)
                    .Select(p => new
                    {
                        id = p.Id,
                        name = p.Name,
                        worksheetName = p.WorksheetName,
                        columns = p.Columns,
                        lastUsedAt = p.LastUsedAt
                    })
                    .ToListAsync(ct);

                return Results.Ok(profiles);
            });

        api.MapGet("/projects/{projectId:guid}/export",
            async (Guid projectId,
                   InventoryExporter exporter,
                   KeystoneDbContext db,
                   CancellationToken ct) =>
            {
                var slug = await db.Projects
                    .AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => p.Slug)
                    .FirstOrDefaultAsync(ct);

                if (slug is null)
                {
                    return Results.NotFound();
                }

                var file = await exporter.ExportAsync(projectId, ct);

                return Results.File(
                    file,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"keystone-{slug}-{DateTime.UtcNow:yyyy-MM-dd}.xlsx");
            });

        // Only admin and sales may change inventory, and an import changes a lot of it at once.
        var editable = api.MapGroup("").RequireAuthorization(AuthPolicies.CanEdit);

        // Form posts carry the session cookie, which is SameSite=Lax and so is not sent on a
        // cross-site POST. There is no CORS policy either, so these are same-origin only.
        editable.MapPost("/projects/{projectId:guid}/import/preview",
            async (Guid projectId,
                   IFormFile file,
                   HttpRequest request,
                   ImportPlanner planner,
                   CancellationToken ct) =>
            {
                if (Refuse(file) is { } refusal)
                {
                    return Results.BadRequest(new { error = refusal });
                }

                using var stream = new MemoryStream();
                await file.CopyToAsync(stream, ct);
                stream.Position = 0;

                var worksheet = request.Form["worksheet"].FirstOrDefault();
                var names = SpreadsheetReader.WorksheetNames(stream);
                stream.Position = 0;

                var sheet = SpreadsheetReader.Read(
                    stream, string.IsNullOrWhiteSpace(worksheet) ? null : worksheet);

                // Whatever mapping the screen has settled on; on the first upload it sends
                // none and the guesses below stand in.
                var mapping = ReadMapping(request.Form["mapping"].FirstOrDefault())
                              ?? Suggest(sheet);

                var plan = await planner.PlanAsync(
                    projectId, sheet, mapping, ImportPlanner.Hash(stream), ct);

                return Results.Ok(new
                {
                    worksheets = names,
                    worksheet = sheet.WorksheetName,
                    headerRow = sheet.HeaderRow,
                    columns = sheet.Columns.Select(c => new
                    {
                        letter = c.Letter,
                        header = c.Header,
                        looksLikeData = c.LooksLikeData,
                        sample = c.Sample,
                        suggested = ImportField.Guess(c.Header)?.Field
                    }),
                    mapping = mapping.Columns,
                    rowCount = sheet.Rows.Count,
                    plan = Render(plan)
                });
            }).DisableAntiforgery();

        editable.MapPost("/projects/{projectId:guid}/import/commit",
            async (Guid projectId,
                   IFormFile file,
                   HttpRequest request,
                   ImportPlanner planner,
                   ImportService importer,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (Refuse(file) is { } refusal)
                {
                    return Results.BadRequest(new { error = refusal });
                }

                using var stream = new MemoryStream();
                await file.CopyToAsync(stream, ct);
                stream.Position = 0;

                var hash = ImportPlanner.Hash(stream);

                if (RefuseUnreviewed(request.Form["hash"].FirstOrDefault(), hash) is { } unreviewed)
                {
                    return Results.BadRequest(new { error = unreviewed });
                }

                var worksheet = request.Form["worksheet"].FirstOrDefault();
                var sheet = SpreadsheetReader.Read(
                    stream, string.IsNullOrWhiteSpace(worksheet) ? null : worksheet);

                var mapping = ReadMapping(request.Form["mapping"].FirstOrDefault()) ?? Suggest(sheet);

                // Re-planned rather than trusting a plan sent back by the browser: the client
                // is not the authority on which units it may write to.
                var plan = await planner.PlanAsync(projectId, sheet, mapping, hash, ct);

                var result = await importer.CommitAsync(
                    projectId, plan, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return Results.Ok(new
                {
                    created = result.Created,
                    updated = result.Updated,
                    skipped = result.Skipped,
                    notes = result.Notes
                });
            }).DisableAntiforgery();

        editable.MapPost("/projects/{projectId:guid}/import/profiles",
            async (Guid projectId,
                   SaveProfileRequest body,
                   KeystoneDbContext db,
                   TimeProvider clock,
                   CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Name))
                {
                    return Results.BadRequest(new { error = "Give the mapping a name." });
                }

                var profile = await db.ImportProfiles
                    .FirstOrDefaultAsync(p => p.ProjectId == projectId && p.Name == body.Name, ct);

                if (profile is null)
                {
                    profile = new ImportProfile { ProjectId = projectId, Name = body.Name.Trim() };
                    db.ImportProfiles.Add(profile);
                }

                profile.Columns = body.Columns;
                profile.WorksheetName = body.Worksheet;
                profile.LastUsedAt = clock.GetUtcNow();

                await db.SaveChangesAsync(ct);

                return Results.Ok(new { id = profile.Id, name = profile.Name });
            });
    }

    /// <summary>
    /// Why this upload may not be committed against the preview it claims, or null if it may.
    /// </summary>
    /// <remarks>
    /// A commit has to name the preview somebody approved. This used to be skipped altogether
    /// when the field was absent, which made "you apply what you read" the caller's to opt into
    /// — and an import rewrites a whole project's prices in one action, with the audit row
    /// recording the commit rather than the review (issue #38). A missing hash is refused in the
    /// same words as a wrong one: in both cases nothing has shown this is the reviewed file, and
    /// the answer is the same either way — upload it again and read the changes.
    /// </remarks>
    internal static string? RefuseUnreviewed(string? claimed, string actual) =>
        string.Equals(claimed, actual, StringComparison.Ordinal)
            ? null
            : "This is not the file that was previewed. Upload it again and read the changes.";

    private static string? Refuse(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return "Choose a file first.";
        }

        if (file.Length > MaxUploadBytes)
        {
            return "That file is larger than an inventory sheet should be.";
        }

        return Path.GetExtension(file.FileName).ToLowerInvariant() is ".xlsx" or ".xlsm"
            ? null
            : "Only .xlsx workbooks can be read. Save the file as one and try again.";
    }

    /// <summary>The mapping to start from when nobody has chosen one yet.</summary>
    private static ImportMapping Suggest(ParsedSheet sheet)
    {
        var columns = new Dictionary<string, string>();

        foreach (var column in sheet.Columns)
        {
            // First column wins. Roka heads two columns "Cod Apartament", and taking the
            // later one would silently read the wrong values.
            if (ImportField.Guess(column.Header) is { } field && !columns.ContainsKey(field.Field))
            {
                columns[field.Field] = column.Letter;
            }
        }

        return new ImportMapping(columns);
    }

    private static ImportMapping? ReadMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var columns = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        return columns is null
            ? null
            : new ImportMapping(columns
                .Where(c => !string.IsNullOrWhiteSpace(c.Value))
                .ToDictionary(c => c.Key, c => c.Value));
    }

    private static object Render(ImportPlan plan) => new
    {
        canCommit = plan.CanCommit,
        creates = plan.Creates,
        updates = plan.Updates,
        unchanged = plan.Unchanged,
        skipped = plan.Skipped,
        failed = plan.Failed,
        problems = plan.Problems,
        newTypes = plan.NewTypes,
        fileHash = plan.FileHash,
        notInFile = plan.NotInFile.Select(u => new { code = u.Code, state = u.State.ToString() }),
        rows = plan.Rows.Select(r => new
        {
            sourceRow = r.SourceRow,
            code = r.Code,
            action = r.Action.ToString(),
            reason = r.Reason,
            staleNote = r.StaleNote,
            changes = r.Changes.ToDictionary(
                c => c.Key,
                c => new { before = c.Value.Before, after = c.Value.After })
        })
    };

    public sealed record SaveProfileRequest(
        string Name,
        Dictionary<string, string> Columns,
        string? Worksheet);
}
