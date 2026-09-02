namespace Hubix.Domain.Common;

/// <summary>
/// Base for every persisted entity.
/// </summary>
/// <remarks>
/// Identifiers are UUIDv7 rather than <see cref="Guid.NewGuid"/>. SQL Server clusters on the
/// primary key by default, and a random GUID inserts into the middle of that index on every
/// write, fragmenting it badly. UUIDv7 is time-ordered, so inserts append.
/// </remarks>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
