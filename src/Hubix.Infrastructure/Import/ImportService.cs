using System.Text.Json;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Import;

/// <summary>What the commit actually did.</summary>
public sealed record ImportResult(
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<string> Notes)
{
    public static ImportResult Refused(string reason) => new(0, 0, 0, [reason]);
}

/// <summary>
/// Applies a plan somebody has read and accepted.
/// </summary>
public sealed class ImportService(
    HubixDbContext db,
    TimeProvider clock,
    ILogger<ImportService> logger)
{
    public async Task<ImportResult> CommitAsync(
        Guid projectId,
        ImportPlan plan,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        if (!plan.CanCommit)
        {
            return ImportResult.Refused("This file has nothing to apply, or has problems to fix first.");
        }

        var now = clock.GetUtcNow();
        var notes = new List<string>();

        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var types = await db.UnitTypes
                .ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, ct);

            foreach (var name in plan.NewTypes)
            {
                if (types.ContainsKey(name))
                {
                    continue;
                }

                var type = new UnitType
                {
                    Name = name,
                    SortOrder = types.Count
                };

                db.UnitTypes.Add(type);
                types[name] = type;
                notes.Add($"Added apartment type \"{name}\".");
            }

            var units = await db.Units
                .Where(u => u.ProjectId == projectId)
                .ToDictionaryAsync(u => u.Code, StringComparer.OrdinalIgnoreCase, ct);

            var created = 0;
            var updated = 0;
            var skipped = 0;

            foreach (var row in plan.Rows)
            {
                if (row.Action is not (RowAction.Create or RowAction.Update) || row.Code is null)
                {
                    continue;
                }

                if (units.TryGetValue(row.Code, out var unit))
                {
                    // Checked again here, not only in the plan. Somebody can click Reserve
                    // while the plan is on screen being read, and the whole point of the rule
                    // is that it holds at the moment of writing.
                    if (unit.State is not (UnitState.Available or UnitState.Withdrawn))
                    {
                        skipped++;
                        notes.Add($"{row.Code} became {unit.State.ToString().ToLowerInvariant()} " +
                                  "while this was being reviewed, so it was left alone.");
                        continue;
                    }

                    Apply(unit, row.Changes, types);
                    unit.UpdatedAt = now;
                    updated++;
                }
                else
                {
                    unit = new Unit
                    {
                        ProjectId = projectId,
                        Code = row.Code,
                        UnitTypeId = Guid.Empty,
                        State = StartingState(row.Changes)
                    };

                    Apply(unit, row.Changes, types);
                    db.Units.Add(unit);
                    units[row.Code] = unit;
                    created++;
                }
            }

            db.AuditLogs.Add(new AuditLog
            {
                ActorType = "staff",
                ActorId = actorId,
                ActorLabel = actorLabel,
                EntityType = "project",
                EntityId = projectId,
                Action = "units.imported",
                After = JsonSerializer.Serialize(new
                {
                    Created = created,
                    Updated = updated,
                    Skipped = skipped,
                    plan.FileHash,
                    Codes = plan.Rows
                        .Where(r => r.Action is RowAction.Create or RowAction.Update)
                        .Select(r => r.Code)
                        .ToList()
                })
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "{Actor} imported {Created} new and {Updated} changed units into project {ProjectId}",
                actorLabel, created, updated, projectId);

            return new ImportResult(created, updated, skipped, notes);
        });
    }

    /// <summary>
    /// What a brand new unit starts as.
    /// </summary>
    /// <remarks>
    /// The only place a file decides a state, and only for a unit that did not exist a moment
    /// ago. On a first import the sheet is the whole truth; after that it never is.
    /// </remarks>
    private static UnitState StartingState(IReadOnlyDictionary<string, ChangedValue> changes) =>
        changes.TryGetValue(ImportField.Available, out var available)
        && available.After is bool free
        && !free
            ? UnitState.Reserved
            : UnitState.Available;

    private static void Apply(
        Unit unit,
        IReadOnlyDictionary<string, ChangedValue> changes,
        IReadOnlyDictionary<string, UnitType> types)
    {
        foreach (var (field, change) in changes)
        {
            switch (field)
            {
                case ImportField.Crt: unit.Crt = (int?)change.After; break;
                case ImportField.Building: unit.Building = (string?)change.After; break;
                case ImportField.Stair: unit.Stair = (string?)change.After; break;
                case ImportField.Floor: unit.FloorLevel = (int?)change.After; break;
                case ImportField.Orientation: unit.Orientation = (string?)change.After; break;
                case ImportField.AreaBuilt: unit.AreaBuilt = (decimal?)change.After; break;
                case ImportField.AreaUsable: unit.AreaUsable = (decimal)change.After!; break;
                case ImportField.AreaBalcony: unit.AreaBalcony = (decimal)change.After!; break;
                case ImportField.BasePriceNet: unit.BasePriceNet = (decimal)change.After!; break;
                case ImportField.CesiunePriceNet: unit.CesiunePriceNet = (decimal?)change.After; break;
                case ImportField.ReducedVat: unit.ReducedVatEligible = (bool)change.After!; break;
                case ImportField.Notes: unit.AdminNotes = (string?)change.After; break;

                case ImportField.TypeName:
                    if (change.After is string name && types.TryGetValue(name, out var type))
                    {
                        unit.UnitType = type;
                        unit.UnitTypeId = type.Id;
                    }

                    break;

                // Availability is read for the note it produces and to seed a new unit's
                // state. It never moves an existing one.
                case ImportField.Available:
                    break;
            }
        }
    }
}
