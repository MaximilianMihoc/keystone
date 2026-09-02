using System.Globalization;
using System.Security.Cryptography;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Infrastructure.Import;

/// <summary>
/// Works out what a spreadsheet would do to a project, without doing any of it.
/// </summary>
/// <remarks>
/// <para>
/// The rule this exists to enforce: <b>a file may never touch a unit somebody is part-way
/// through buying</b>. Once a unit leaves Available it is described by a reservation with a
/// frozen price and a signed agreement behind it, and a spreadsheet arriving by email is not
/// allowed to contradict that. Those rows are skipped and reported, never applied.
/// </para>
/// <para>
/// State is not importable either. A file can decide what a <em>new</em> unit starts as, since
/// on a first import the sheet is all there is, but it can never move an existing one. Where
/// the two disagree the row carries a note saying so, which is how a stale sheet gets noticed.
/// </para>
/// </remarks>
public sealed class ImportPlanner(HubixDbContext db)
{
    /// <summary>Units the file is allowed to change.</summary>
    /// <remarks>
    /// Withdrawn is here because nobody is relying on its price; it is off sale, not sold.
    /// </remarks>
    private static readonly UnitState[] Writable = [UnitState.Available, UnitState.Withdrawn];

    public async Task<ImportPlan> PlanAsync(
        Guid projectId,
        ParsedSheet sheet,
        ImportMapping mapping,
        string fileHash,
        CancellationToken ct)
    {
        var problems = new List<string>();

        foreach (var field in mapping.Missing())
        {
            problems.Add($"{field.Label} is needed and no column is mapped to it.");
        }

        var project = await db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId, ct);

        if (!project)
        {
            problems.Add("That project no longer exists.");
        }

        if (problems.Count > 0)
        {
            return new ImportPlan([], [], [], problems, fileHash);
        }

        var existing = await db.Units
            .AsNoTracking()
            .Where(u => u.ProjectId == projectId)
            .Select(u => new
            {
                u.Id,
                u.Code,
                u.State,
                u.Crt,
                u.Building,
                u.Stair,
                u.FloorLevel,
                TypeName = u.UnitType!.Name,
                u.Orientation,
                u.AreaBuilt,
                u.AreaUsable,
                u.AreaBalcony,
                u.BasePriceNet,
                u.CesiunePriceNet,
                u.ReducedVatEligible,
                u.ParkingPriceNet,
                u.ParkingAdvanceNet,
                u.AdminNotes
            })
            .ToListAsync(ct);

        var byCode = existing.ToDictionary(u => u.Code, StringComparer.OrdinalIgnoreCase);

        var knownTypes = await db.UnitTypes
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Select(t => t.Name)
            .ToListAsync(ct);

        var newTypes = new List<string>();
        var rows = new List<PlannedRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in sheet.Rows)
        {
            var code = Value(row, mapping, ImportField.Code)?.Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                rows.Add(new PlannedRow(row.Number, null, RowAction.Failed,
                    "No apartment code in this row.", new Dictionary<string, ChangedValue>()));
                continue;
            }

            if (!seen.Add(code))
            {
                // Two rows claiming the same apartment: the file disagrees with itself, and
                // picking one silently would be a coin toss.
                rows.Add(new PlannedRow(row.Number, code, RowAction.Failed,
                    $"{code} appears more than once in this file.",
                    new Dictionary<string, ChangedValue>()));
                continue;
            }

            var values = new Dictionary<string, object?>();
            var badColumn = ReadValues(row, mapping, values);

            if (badColumn is not null)
            {
                rows.Add(new PlannedRow(row.Number, code, RowAction.Failed, badColumn,
                    new Dictionary<string, ChangedValue>()));
                continue;
            }

            var typeName = values.GetValueOrDefault(ImportField.TypeName) as string;

            if (string.IsNullOrWhiteSpace(typeName))
            {
                rows.Add(new PlannedRow(row.Number, code, RowAction.Failed,
                    "No apartment type in this row.", new Dictionary<string, ChangedValue>()));
                continue;
            }

            if (!knownTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase)
                && !newTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase))
            {
                // Created on commit, but listed first: an unfamiliar type is usually a typo,
                // and this is where it becomes visible.
                newTypes.Add(typeName);
            }

            if (!byCode.TryGetValue(code, out var unit))
            {
                rows.Add(new PlannedRow(row.Number, code, RowAction.Create, null,
                    values.Where(v => v.Value is not null)
                        .ToDictionary(v => v.Key, v => new ChangedValue(null, v.Value))));
                continue;
            }

            if (!Writable.Contains(unit.State))
            {
                rows.Add(new PlannedRow(row.Number, code, RowAction.Skipped,
                    $"{code} is {unit.State.ToString().ToLowerInvariant()}; a file cannot change it.",
                    new Dictionary<string, ChangedValue>()));
                continue;
            }

            var changes = new Dictionary<string, ChangedValue>();
            Compare(changes, ImportField.Crt, unit.Crt, values);
            Compare(changes, ImportField.Building, unit.Building, values);
            Compare(changes, ImportField.Stair, unit.Stair, values);
            Compare(changes, ImportField.Floor, unit.FloorLevel, values);
            Compare(changes, ImportField.TypeName, unit.TypeName, values);
            Compare(changes, ImportField.Orientation, unit.Orientation, values);
            Compare(changes, ImportField.AreaBuilt, unit.AreaBuilt, values);
            Compare(changes, ImportField.AreaUsable, unit.AreaUsable, values);
            Compare(changes, ImportField.AreaBalcony, unit.AreaBalcony, values);
            Compare(changes, ImportField.BasePriceNet, unit.BasePriceNet, values);
            Compare(changes, ImportField.CesiunePriceNet, unit.CesiunePriceNet, values);
            Compare(changes, ImportField.ReducedVat, unit.ReducedVatEligible, values);
            Compare(changes, ImportField.ParkingPriceNet, unit.ParkingPriceNet, values);
            Compare(changes, ImportField.ParkingAdvanceNet, unit.ParkingAdvanceNet, values);
            Compare(changes, ImportField.Notes, unit.AdminNotes, values);

            rows.Add(new PlannedRow(
                row.Number,
                code,
                changes.Count == 0 ? RowAction.Unchanged : RowAction.Update,
                null,
                changes)
            {
                StaleNote = AvailabilityNote(values, unit.State, code)
            });
        }

        var notInFile = existing
            .Where(u => !seen.Contains(u.Code))
            .Select(u => new UnmatchedUnit(u.Code, u.State))
            .OrderBy(u => u.Code)
            .ToList();

        return new ImportPlan(rows, notInFile, newTypes, [], fileHash);
    }

    /// <summary>
    /// Where the file's availability column disagrees with what this system knows.
    /// </summary>
    /// <remarks>
    /// Reported and never acted on. A unit is Held because somebody clicked Reserve two hours
    /// ago; a sheet last saved on Tuesday saying otherwise is out of date, not authoritative.
    /// </remarks>
    private static string? AvailabilityNote(
        IReadOnlyDictionary<string, object?> values,
        UnitState state,
        string code)
    {
        if (values.GetValueOrDefault(ImportField.Available) is not bool saysAvailable)
        {
            return null;
        }

        var isAvailable = state == UnitState.Available;

        if (saysAvailable == isAvailable)
        {
            return null;
        }

        return saysAvailable
            ? $"The file says {code} is available; here it is {state.ToString().ToLowerInvariant()}."
            : $"The file says {code} is taken; here it is available.";
    }

    private static string? Value(SheetRow row, ImportMapping mapping, string field) =>
        mapping.ColumnFor(field) is { } letter ? row[letter] : null;

    /// <summary>Reads every mapped column. Returns the first unreadable one, or null.</summary>
    private static string? ReadValues(
        SheetRow row,
        ImportMapping mapping,
        Dictionary<string, object?> values)
    {
        foreach (var field in ImportField.All)
        {
            var raw = Value(row, mapping, field.Field);

            if (string.IsNullOrWhiteSpace(raw))
            {
                values[field.Field] = null;
                continue;
            }

            switch (field.Field)
            {
                case ImportField.Crt or ImportField.Floor:
                    if (ParseNumber(raw) is not { } whole)
                    {
                        return $"{field.Label}: \"{raw}\" is not a number.";
                    }

                    values[field.Field] = (int)Math.Round(whole);
                    break;

                case ImportField.AreaBuilt or ImportField.AreaUsable or ImportField.AreaBalcony
                    or ImportField.BasePriceNet or ImportField.CesiunePriceNet
                    or ImportField.ParkingPriceNet or ImportField.ParkingAdvanceNet:
                    if (ParseNumber(raw) is not { } number)
                    {
                        return $"{field.Label}: \"{raw}\" is not a number.";
                    }

                    values[field.Field] = number;
                    break;

                case ImportField.ReducedVat or ImportField.Available:
                    values[field.Field] = ParseYesNo(raw);
                    break;

                default:
                    values[field.Field] = raw.Trim();
                    break;
            }
        }

        return null;
    }

    private static void Compare(
        Dictionary<string, ChangedValue> changes,
        string field,
        object? current,
        IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue(field, out var incoming) || incoming is null)
        {
            // A blank cell means "not stated", never "clear this". Half these sheets leave
            // whole columns empty for whole projects.
            return;
        }

        if (Same(current, incoming))
        {
            return;
        }

        changes[field] = new ChangedValue(current, incoming);
    }

    private static bool Same(object? current, object? incoming) => (current, incoming) switch
    {
        (null, _) => false,
        (decimal a, decimal b) => a == b,
        (decimal a, int b) => a == b,
        (int a, int b) => a == b,
        (bool a, bool b) => a == b,
        (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
        _ => Equals(current, incoming)
    };

    /// <summary>
    /// A number as somebody typed it.
    /// </summary>
    /// <remarks>
    /// These files are edited in two locales. "1.234,56" and "1,234.56" are the same price, and
    /// whichever separator comes last is the decimal one. A lone separator with exactly three
    /// digits after it is thousands — "135.776" is a price, not 135 and a bit.
    /// </remarks>
    public static decimal? ParseNumber(string raw)
    {
        var text = new string(raw.Where(c => !char.IsWhiteSpace(c) && c != '€' && c != '%').ToArray());

        if (text.Length == 0)
        {
            return null;
        }

        var lastDot = text.LastIndexOf('.');
        var lastComma = text.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            var decimalPoint = Math.Max(lastDot, lastComma);
            var thousands = decimalPoint == lastDot ? ',' : '.';

            text = text.Replace(thousands.ToString(), "")
                       .Replace(decimalPoint == lastDot ? "." : ",", ".");
        }
        else if (lastDot >= 0 || lastComma >= 0)
        {
            var at = Math.Max(lastDot, lastComma);
            var after = text.Length - at - 1;

            text = after == 3
                ? text.Remove(at, 1)
                : text[..at] + "." + text[(at + 1)..];
        }

        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Hubix writes DA and NU. Anything else is a no.</summary>
    public static bool ParseYesNo(string raw) =>
        raw.Trim().ToLowerInvariant() is "da" or "yes" or "true" or "1" or "x";

    /// <summary>
    /// Identifies the exact bytes a plan was made from.
    /// </summary>
    /// <remarks>
    /// The commit re-reads the file rather than trusting a stashed copy, so this is what makes
    /// "apply what I just read" true: a different file produces a different hash and is refused.
    /// </remarks>
    public static string Hash(Stream stream)
    {
        var position = stream.Position;
        stream.Position = 0;

        var hash = Convert.ToHexString(SHA256.HashData(stream));

        stream.Position = position;
        return hash;
    }
}
