using System.Security.Claims;
using Hubix.Domain;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Assets;
using Hubix.Web.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Web.Api;

/// <summary>
/// Project files: uploading them, saying what they belong to, and reading them back.
/// </summary>
public static class AssetApi
{
    /// <summary>
    /// Serves a file from the local store, if the link is genuine and current.
    /// </summary>
    /// <remarks>
    /// Deliberately outside the admin group and open to anybody holding a valid link: floor
    /// plans and RLVs are shown to prospects on the public site, and requiring a session to
    /// load an image on a public page would defeat the page. The signature is the credential,
    /// and it expires.
    /// <para>
    /// Not mapped at all when blob storage is configured, because there the link points
    /// straight at Azure and nothing comes through here.
    /// </para>
    /// </remarks>
    public static void MapAssetFiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/assets/{**key}", async (
            string key,
            long expires,
            string signature,
            IAssetStorage storage,
            CancellationToken ct) =>
        {
            if (storage is not LocalAssetStorage local)
            {
                return Results.NotFound();
            }

            if (!local.IsValid(key, expires, signature))
            {
                // One answer for a forged signature and an expired one. Telling them apart
                // tells somebody probing which half they got right.
                return Results.NotFound();
            }

            var stream = await local.OpenAsync(key, ct);

            if (stream is null)
            {
                return Results.NotFound();
            }

            // Inline so a plan opens in the browser rather than landing in Downloads.
            return Results.File(stream, ContentTypeFor(key), enableRangeProcessing: true);
        });
    }

    public static void MapAssets(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{projectId:guid}/assets",
            async (Guid projectId,
                   HubixDbContext db,
                   AssetService assets,
                   CancellationToken ct) =>
            {
                var stored = await db.Assets
                    .AsNoTracking()
                    .Where(a => a.ProjectId == projectId)
                    .OrderBy(a => a.Kind).ThenBy(a => a.SortOrder).ThenBy(a => a.FileName)
                    .Select(a => new
                    {
                        a.Id,
                        a.Kind,
                        a.FileName,
                        a.ContentType,
                        a.SizeBytes,
                        a.Caption,
                        a.StorageKey,
                        a.CreatedAt,
                        UnitIds = a.Units.Select(u => u.UnitId).ToList()
                    })
                    .ToListAsync(ct);

                var floors = await db.Floors
                    .AsNoTracking()
                    .Where(f => f.ProjectId == projectId)
                    .OrderBy(f => f.Building).ThenBy(f => f.Level)
                    .Select(f => new
                    {
                        building = f.Building,
                        level = f.Level,
                        floorPlanAssetId = f.FloorPlanAssetId
                    })
                    .ToListAsync(ct);

                // Levels that exist because units sit on them, whether or not a floor record
                // has ever been written. Otherwise the screen offers nothing to attach to.
                var levels = await db.Units
                    .AsNoTracking()
                    .Where(u => u.ProjectId == projectId && u.FloorLevel != null)
                    .Select(u => new { building = u.Building, level = u.FloorLevel!.Value })
                    .Distinct()
                    .OrderBy(l => l.building).ThenBy(l => l.level)
                    .ToListAsync(ct);

                var listed = new List<object>(stored.Count);

                foreach (var asset in stored)
                {
                    listed.Add(new
                    {
                        id = asset.Id,
                        kind = asset.Kind.ToString(),
                        fileName = asset.FileName,
                        contentType = asset.ContentType,
                        sizeBytes = asset.SizeBytes,
                        caption = asset.Caption,
                        createdAt = asset.CreatedAt,
                        unitIds = asset.UnitIds,
                        url = await assets.LinkAsync(asset.StorageKey, ct)
                    });
                }

                return Results.Ok(new { assets = listed, floors, levels });
            });

        var editable = api.MapGroup("").RequireAuthorization(AuthPolicies.CanEdit);

        editable.MapPost("/projects/{projectId:guid}/assets",
            async (Guid projectId,
                   IFormFile file,
                   HttpRequest request,
                   AssetService assets,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
            {
                if (file is null || file.Length == 0)
                {
                    return Results.BadRequest(new { error = "Choose a file first." });
                }

                if (!Enum.TryParse<AssetKind>(request.Form["kind"].FirstOrDefault(), out var kind))
                {
                    kind = AssetKind.Other;
                }

                await using var content = file.OpenReadStream();

                var result = await assets.UploadAsync(
                    projectId, kind, file.FileName, content, file.Length,
                    request.Form["caption"].FirstOrDefault(),
                    AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct);

                return result switch
                {
                    AssetOutcome.Saved saved => Results.Ok(new { id = saved.AssetId, fileName = saved.FileName }),
                    AssetOutcome.NotFound => Results.NotFound(),
                    AssetOutcome.Refused refused => Results.BadRequest(new { error = refused.Reason }),
                    _ => Results.Problem()
                };
            }).DisableAntiforgery();

        editable.MapPost("/projects/{projectId:guid}/floors/plan",
            async (Guid projectId,
                   FloorPlanRequest body,
                   AssetService assets,
                   CancellationToken ct) =>
                Render(await assets.SetFloorPlanAsync(
                    projectId, body.Building, body.Level, body.AssetId, ct)));

        editable.MapPost("/units/{unitId:guid}/assets/{assetId:guid}",
            async (Guid unitId, Guid assetId, AssetService assets, CancellationToken ct) =>
                Render(await assets.AttachToUnitAsync(unitId, assetId, ct)));

        editable.MapDelete("/units/{unitId:guid}/assets/{assetId:guid}",
            async (Guid unitId, Guid assetId, AssetService assets, CancellationToken ct) =>
                Render(await assets.DetachFromUnitAsync(unitId, assetId, ct)));

        editable.MapDelete("/assets/{assetId:guid}",
            async (Guid assetId,
                   AssetService assets,
                   ClaimsPrincipal user,
                   CancellationToken ct) =>
                Render(await assets.DeleteAsync(
                    assetId, AdminApi.ActorId(user), AdminApi.ActorLabel(user), ct)));
    }

    private static IResult Render(AssetOutcome outcome) => outcome switch
    {
        AssetOutcome.Attached or AssetOutcome.Removed => Results.Ok(new { ok = true }),
        AssetOutcome.Saved saved => Results.Ok(new { id = saved.AssetId }),
        AssetOutcome.NotFound => Results.NotFound(),
        AssetOutcome.Refused refused => Results.BadRequest(new { error = refused.Reason }),
        _ => Results.Problem()
    };

    private static string ContentTypeFor(string key) =>
        Path.GetExtension(key).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };

    public sealed record FloorPlanRequest(string? Building, int Level, Guid? AssetId);
}
