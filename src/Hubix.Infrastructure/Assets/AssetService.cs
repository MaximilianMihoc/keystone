using System.Text.Json;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hubix.Infrastructure.Assets;

public abstract record AssetOutcome
{
    public sealed record Saved(Guid AssetId, string FileName) : AssetOutcome;

    public sealed record Attached : AssetOutcome;

    public sealed record Removed : AssetOutcome;

    public sealed record Changed : AssetOutcome;

    public sealed record NotFound : AssetOutcome;

    public sealed record Refused(string Reason) : AssetOutcome;
}

/// <summary>
/// Uploading project files and deciding what they belong to.
/// </summary>
/// <remarks>
/// The point of the whole thing: a floor plan is uploaded once and every apartment on that
/// floor shows it. In the spreadsheets this replaces, a Drive link is pasted into each row,
/// which is why some rows have the wrong plan and some have none.
/// </remarks>
public sealed class AssetService(
    HubixDbContext db,
    IAssetStorage storage,
    IOptions<AssetOptions> options,
    TimeProvider clock,
    ILogger<AssetService> logger)
{
    private readonly AssetOptions _options = options.Value;

    /// <summary>
    /// What may be uploaded.
    /// </summary>
    /// <remarks>
    /// An allow list, not a block list. These are drawings and brochures; anything else
    /// arriving is a mistake at best, and a file the browser might execute at worst.
    /// </remarks>
    private static readonly Dictionary<string, string> Permitted = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml"
    };

    public async Task<AssetOutcome> UploadAsync(
        Guid projectId,
        AssetKind kind,
        string fileName,
        Stream content,
        long sizeBytes,
        string? caption,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var extension = Path.GetExtension(fileName);

        if (!Permitted.TryGetValue(extension, out var contentType))
        {
            return new AssetOutcome.Refused(
                $"{extension} files cannot be uploaded. Use a PDF or an image.");
        }

        if (sizeBytes > _options.MaxUploadMegabytes * 1024L * 1024L)
        {
            return new AssetOutcome.Refused(
                $"That file is larger than {_options.MaxUploadMegabytes} MB.");
        }

        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            return new AssetOutcome.NotFound();
        }

        // Keyed by a fresh id rather than the uploaded name: two people uploading "plan.pdf"
        // must not overwrite each other, and a name from a form has no business becoming a path.
        var asset = new Asset
        {
            ProjectId = projectId,
            Kind = kind,
            StorageKey = string.Empty,
            FileName = Path.GetFileName(fileName),
            ContentType = contentType,
            Caption = caption
        };

        asset.StorageKey = $"{projectId}/{asset.Id}{extension.ToLowerInvariant()}";

        var stored = await storage.SaveAsync(asset.StorageKey, content, contentType, ct);
        asset.SizeBytes = stored.SizeBytes;

        db.Assets.Add(asset);

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "asset",
            EntityId = asset.Id,
            Action = "asset.uploaded",
            After = JsonSerializer.Serialize(new
            {
                Kind = kind.ToString(),
                asset.FileName,
                asset.SizeBytes
            })
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Actor} uploaded {FileName} ({Kind}) to project {ProjectId}",
            actorLabel, asset.FileName, kind, projectId);

        return new AssetOutcome.Saved(asset.Id, asset.FileName);
    }

    /// <summary>
    /// Puts a plan on a floor, creating the floor if this is the first thing said about it.
    /// </summary>
    /// <remarks>
    /// One plan per floor, and every unit on that floor inherits it. That inheritance is the
    /// reason floors exist as records at all.
    /// </remarks>
    public async Task<AssetOutcome> SetFloorPlanAsync(
        Guid projectId,
        string? building,
        int level,
        Guid? assetId,
        CancellationToken ct)
    {
        if (assetId is { } id
            && !await db.Assets.AnyAsync(a => a.Id == id && a.ProjectId == projectId, ct))
        {
            return new AssetOutcome.Refused("That file belongs to another project.");
        }

        // Empty and null are the same building — one of them arriving from a form must not
        // create a second floor 1.
        var named = string.IsNullOrWhiteSpace(building) ? null : building.Trim();

        var floor = await db.Floors
            .FirstOrDefaultAsync(f => f.ProjectId == projectId
                                      && f.Building == named
                                      && f.Level == level, ct);

        if (floor is null)
        {
            floor = new Floor { ProjectId = projectId, Building = named, Level = level };
            db.Floors.Add(floor);
        }

        floor.FloorPlanAssetId = assetId;
        floor.UpdatedAt = clock.GetUtcNow();

        await db.SaveChangesAsync(ct);

        return new AssetOutcome.Attached();
    }

    /// <summary>Refiles an upload under another kind, because the kind is guessed at upload.</summary>
    /// <remarks>
    /// A plan still carried by a floor stays a floor plan. Clearing those floors here would
    /// quietly take the plan off every apartment on them; saying so lets a person decide.
    /// </remarks>
    public async Task<AssetOutcome> ChangeKindAsync(
        Guid assetId,
        AssetKind kind,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct);

        if (asset is null)
        {
            return new AssetOutcome.NotFound();
        }

        if (asset.Kind == kind)
        {
            return new AssetOutcome.Changed();
        }

        if (kind != AssetKind.FloorPlan && await db.Floors.AnyAsync(f => f.FloorPlanAssetId == assetId, ct))
        {
            return new AssetOutcome.Refused("This plan is on a floor. Take it off its floors first.");
        }

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "asset",
            EntityId = assetId,
            Action = "asset.kind_changed",
            Before = JsonSerializer.Serialize(new { Kind = asset.Kind.ToString() }),
            After = JsonSerializer.Serialize(new { Kind = kind.ToString() })
        });

        asset.Kind = kind;
        await db.SaveChangesAsync(ct);

        return new AssetOutcome.Changed();
    }

    /// <summary>Attaches a file to one apartment — its RLV, or a render of it.</summary>
    public async Task<AssetOutcome> AttachToUnitAsync(
        Guid unitId,
        Guid assetId,
        CancellationToken ct)
    {
        var unit = await db.Units
            .AsNoTracking()
            .Where(u => u.Id == unitId)
            .Select(u => new { u.ProjectId })
            .FirstOrDefaultAsync(ct);

        if (unit is null)
        {
            return new AssetOutcome.NotFound();
        }

        if (!await db.Assets.AnyAsync(a => a.Id == assetId && a.ProjectId == unit.ProjectId, ct))
        {
            return new AssetOutcome.Refused("That file belongs to another project.");
        }

        if (await db.UnitAssets.AnyAsync(ua => ua.UnitId == unitId && ua.AssetId == assetId, ct))
        {
            // Already attached. Saying so beats a constraint violation.
            return new AssetOutcome.Attached();
        }

        db.UnitAssets.Add(new UnitAsset { UnitId = unitId, AssetId = assetId });
        await db.SaveChangesAsync(ct);

        return new AssetOutcome.Attached();
    }

    public async Task<AssetOutcome> DetachFromUnitAsync(
        Guid unitId,
        Guid assetId,
        CancellationToken ct)
    {
        var removed = await db.UnitAssets
            .Where(ua => ua.UnitId == unitId && ua.AssetId == assetId)
            .ExecuteDeleteAsync(ct);

        return removed > 0 ? new AssetOutcome.Removed() : new AssetOutcome.NotFound();
    }

    /// <summary>
    /// Deletes a file and everything pointing at it.
    /// </summary>
    /// <remarks>
    /// The database row goes first and the blob second. The other order can leave a row
    /// pointing at a file that is gone, which renders as a broken plan on a public page;
    /// this order can at worst leave a file nobody references, which costs pennies.
    /// </remarks>
    public async Task<AssetOutcome> DeleteAsync(
        Guid assetId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var asset = await db.Assets
            .AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new { a.StorageKey, a.FileName, a.Kind })
            .FirstOrDefaultAsync(ct);

        if (asset is null)
        {
            return new AssetOutcome.NotFound();
        }

        // All three removals go straight to the database rather than through the tracker.
        // Removing a tracked Asset severs its UnitAsset links in memory, and EF then refuses
        // the save because those links are required — which it should, since a link with no
        // file is meaningless. Deleting the links outright says the same thing without the
        // argument.
        await db.Floors
            .Where(f => f.FloorPlanAssetId == assetId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.FloorPlanAssetId, (Guid?)null), ct);

        await db.UnitAssets.Where(ua => ua.AssetId == assetId).ExecuteDeleteAsync(ct);
        await db.Assets.Where(a => a.Id == assetId).ExecuteDeleteAsync(ct);

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "asset",
            EntityId = assetId,
            Action = "asset.deleted",
            Before = JsonSerializer.Serialize(new { asset.FileName, Kind = asset.Kind.ToString() })
        });

        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(asset.StorageKey, ct);

        logger.LogInformation("{Actor} deleted {FileName}", actorLabel, asset.FileName);

        return new AssetOutcome.Removed();
    }

    /// <summary>A link to read a file, good for as long as configuration says.</summary>
    public Task<string> LinkAsync(string storageKey, CancellationToken ct) =>
        storage.GetReadUrlAsync(
            storageKey, TimeSpan.FromMinutes(_options.LinkLifetimeMinutes), ct);
}
