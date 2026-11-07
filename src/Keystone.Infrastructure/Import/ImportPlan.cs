using Keystone.Domain;

namespace Keystone.Infrastructure.Import;

/// <summary>Which column each field is read from. Field key to Excel column letter.</summary>
public sealed record ImportMapping(IReadOnlyDictionary<string, string> Columns)
{
    public string? ColumnFor(string field) =>
        Columns.TryGetValue(field, out var letter) ? letter : null;

    public bool Has(string field) => ColumnFor(field) is not null;

    /// <summary>Required fields with no column behind them.</summary>
    public IReadOnlyList<ImportField> Missing() =>
        ImportField.All.Where(f => f.Required && !Has(f.Field)).ToList();
}

public enum RowAction
{
    /// <summary>A unit this project does not have yet.</summary>
    Create,

    /// <summary>An existing unit with something to change.</summary>
    Update,

    /// <summary>Present and identical. Most of a re-import is this.</summary>
    Unchanged,

    /// <summary>Somebody is part-way through buying it, so the file is not allowed near it.</summary>
    Skipped,

    /// <summary>The row could not be read.</summary>
    Failed
}

/// <summary>What a field would change from and to.</summary>
public sealed record ChangedValue(object? Before, object? After);

/// <summary>One row of the file, and what the import would do with it.</summary>
public sealed record PlannedRow(
    int SourceRow,
    string? Code,
    RowAction Action,
    string? Reason,
    IReadOnlyDictionary<string, ChangedValue> Changes)
{
    /// <summary>
    /// Where the file and this system disagree about availability, on a row that will not be
    /// changed. Not an error — it is the most useful thing an import can tell anybody, because
    /// it is exactly where the spreadsheet has gone stale.
    /// </summary>
    public string? StaleNote { get; init; }
}

/// <summary>A unit in the project that the file did not mention.</summary>
public sealed record UnmatchedUnit(string Code, UnitState State);

/// <summary>
/// The dry run.
/// </summary>
/// <remarks>
/// Every import produces one of these first, and nothing is written until somebody has read it
/// and said yes. The spreadsheets this replaces are edited by several people at once and mailed
/// around; an importer that applied them unseen would be a faster way to make the same mistakes.
/// </remarks>
public sealed record ImportPlan(
    IReadOnlyList<PlannedRow> Rows,
    IReadOnlyList<UnmatchedUnit> NotInFile,
    IReadOnlyList<string> NewTypes,
    IReadOnlyList<string> Problems,
    string FileHash)
{
    public int Creates => Rows.Count(r => r.Action == RowAction.Create);

    public int Updates => Rows.Count(r => r.Action == RowAction.Update);

    public int Unchanged => Rows.Count(r => r.Action == RowAction.Unchanged);

    public int Skipped => Rows.Count(r => r.Action == RowAction.Skipped);

    public int Failed => Rows.Count(r => r.Action == RowAction.Failed);

    /// <summary>Whether committing is allowed at all.</summary>
    public bool CanCommit => Problems.Count == 0 && Failed == 0 && (Creates + Updates) > 0;
}
