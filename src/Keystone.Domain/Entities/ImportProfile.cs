using Keystone.Domain.Common;

namespace Keystone.Domain.Entities;

/// <summary>
/// A remembered column mapping for one project's spreadsheet.
/// </summary>
/// <remarks>
/// The operator re-sends the same workbook every time anything changes. Making somebody re-map thirty
/// columns on every upload would guarantee the importer went unused, and a mapping done in a
/// hurry is how a price lands in the balcony column.
/// </remarks>
public class ImportProfile : Entity
{
    public Guid ProjectId { get; set; }

    public Project? Project { get; set; }

    public required string Name { get; set; }

    /// <summary>Field key to Excel column letter, stored as JSON.</summary>
    public IReadOnlyDictionary<string, string> Columns { get; set; } =
        new Dictionary<string, string>();

    /// <summary>Which sheet of the workbook, where there is more than one.</summary>
    public string? WorksheetName { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
