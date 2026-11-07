using Keystone.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Infrastructure.Admin;

/// <summary>What to narrow the log to. Every field is optional; all of them combine with AND.</summary>
/// <param name="EntityId">One thing's whole history, which is the question the table exists for.</param>
/// <param name="Actor">Matches the actor label loosely, so an email fragment is enough.</param>
/// <param name="From">Inclusive.</param>
/// <param name="To">Exclusive, so a day filter is From = the day, To = the next.</param>
public sealed record AuditFilter(
    Guid? EntityId = null,
    string? EntityType = null,
    string? Action = null,
    string? ActorType = null,
    string? Actor = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int? Take = null);

/// <summary>One line of the log.</summary>
public sealed record AuditRow(
    DateTimeOffset At,
    string Actor,
    string ActorType,
    string Action,
    string EntityType,
    Guid EntityId,
    string? Before,
    string? After);

/// <summary>
/// An action, and the entity type it was recorded against.
/// </summary>
/// <remarks>
/// Paired rather than two flat lists, because the action name does not tell you the entity
/// type: <c>floor.created</c>, <c>pricing.created</c> and <c>translation.saved</c> are all
/// filed under <c>project</c>. A filter that assumed the prefix would quietly hide them.
/// </remarks>
public sealed record AuditActionKind(string EntityType, string Action);

/// <summary>The vocabulary actually present in the log, for the filter dropdowns.</summary>
public sealed record AuditKinds(
    IReadOnlyList<string> EntityTypes,
    IReadOnlyList<AuditActionKind> Actions,
    IReadOnlyList<string> ActorTypes);

/// <summary>
/// Reading the audit log.
/// </summary>
/// <remarks>
/// <para>
/// A service rather than a query inlined in the endpoint, unlike the other lists here, because
/// this one has logic worth testing: seven optional filters that have to combine, and a bound
/// that has to hold whatever is asked for. Endpoints in this system have no tests at all, so
/// logic left in one is logic nothing checks.
/// </para>
/// <para>
/// Read-only by design. The table is append-only — every row is somebody's answer to "who did
/// this" — so there is no edit or delete here, and there should not be.
/// </para>
/// </remarks>
public sealed class AuditService(KeystoneDbContext db)
{
    public const int DefaultTake = 50;

    public const int MaxTake = 500;

    public async Task<IReadOnlyList<AuditRow>> SearchAsync(AuditFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = db.AuditLogs.AsNoTracking();

        if (filter.EntityId is { } entityId)
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        if (Given(filter.EntityType) is { } entityType)
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        // Prefix rather than equality: "reservation." is how somebody asks for everything that
        // happened to reservations, and the vocabulary is built to be read that way.
        if (Given(filter.Action) is { } action)
        {
            query = action.EndsWith('.')
                ? query.Where(a => a.Action.StartsWith(action))
                : query.Where(a => a.Action == action);
        }

        if (Given(filter.ActorType) is { } actorType)
        {
            query = query.Where(a => a.ActorType == actorType);
        }

        // Loose, because what anybody remembers is part of an address. The collation is
        // case-insensitive, so this needs no ToLower.
        if (Given(filter.Actor) is { } actor)
        {
            query = query.Where(a => a.ActorLabel != null && a.ActorLabel.Contains(actor));
        }

        if (filter.From is { } from)
        {
            query = query.Where(a => a.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(a => a.CreatedAt < to);
        }

        return await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(filter.Take ?? DefaultTake, 1, MaxTake))
            .Select(a => new AuditRow(
                a.CreatedAt,
                a.ActorLabel ?? a.ActorType,
                a.ActorType,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.Before,
                a.After))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The entity types, actions and actor types the log actually contains.
    /// </summary>
    /// <remarks>
    /// Read from the rows rather than listed in the client. The action vocabulary is spread
    /// across every service that writes one, so a copy in TypeScript would be a second place to
    /// update and the first to go stale — and it would offer filters that match nothing.
    /// </remarks>
    public async Task<AuditKinds> KindsAsync(CancellationToken ct)
    {
        // An anonymous type on the way out, not the record: SQL Server can DISTINCT a pair of
        // columns, but EF cannot translate Distinct() over a constructor call.
        var pairs = await db.AuditLogs
            .Select(a => new { a.EntityType, a.Action })
            .Distinct()
            .OrderBy(a => a.Action)
            .ToListAsync(ct);

        return new AuditKinds(
            await db.AuditLogs.Select(a => a.EntityType).Distinct().OrderBy(t => t).ToListAsync(ct),
            pairs.Select(p => new AuditActionKind(p.EntityType, p.Action)).ToList(),
            await db.AuditLogs.Select(a => a.ActorType).Distinct().OrderBy(t => t).ToListAsync(ct));
    }

    private static string? Given(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
