using Hubix.Domain.Common;

namespace Hubix.Domain.Entities;

/// <summary>
/// A stored file: floor plan, RLV, render, site plan, prospectus.
/// </summary>
public class Asset : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public AssetKind Kind { get; set; } = AssetKind.Other;

    /// <summary>Key in blob storage. Never a public URL; those are signed on demand.</summary>
    public required string StorageKey { get; set; }

    public required string FileName { get; set; }

    public string? ContentType { get; set; }

    public long SizeBytes { get; set; }

    public string? Caption { get; set; }

    public int SortOrder { get; set; }

    public ICollection<UnitAsset> Units { get; set; } = [];
}

/// <summary>Links a unit to an asset — chiefly its RLV.</summary>
public class UnitAsset : Entity
{
    public Guid UnitId { get; set; }

    public Unit? Unit { get; set; }

    public Guid AssetId { get; set; }

    public Asset? Asset { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Append-only record of every state change.
/// </summary>
/// <remarks>
/// Exists so that "who released that hold, and when?" is answerable. In the spreadsheets this
/// replaces, the answer is that nobody knows.
/// </remarks>
public class AuditLog : Entity
{
    /// <summary>"staff", "prospect" or "system".</summary>
    public required string ActorType { get; set; }

    public Guid? ActorId { get; set; }

    public string? ActorLabel { get; set; }

    /// <summary>"unit", "reservation", "hold".</summary>
    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary>"hold.created", "unit.state_changed", "reservation.cancelled".</summary>
    public required string Action { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }

    public string? IpAddress { get; set; }
}
