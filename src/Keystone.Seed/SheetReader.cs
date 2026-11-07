using System.Globalization;
using System.Text.Json;

namespace Keystone.Seed;

/// <summary>One cell: the header it sat under, and its raw value.</summary>
public sealed record Cell(string Column, string? Header, string Value);

/// <summary>One spreadsheet row, addressable by column letter or by header text.</summary>
public sealed class SheetRow(int sourceRow, IReadOnlyList<Cell> cells)
{
    public int SourceRow { get; } = sourceRow;

    public IReadOnlyList<Cell> Cells { get; } = cells;

    public string? ByColumn(string column) =>
        Cells.FirstOrDefault(c => c.Column == column)?.Value;

    /// <summary>
    /// First cell whose header matches any of the given names, compared loosely.
    /// </summary>
    /// <remarks>
    /// Matching by header rather than column letter is what lets one reader handle five
    /// spreadsheets whose layouts have drifted apart. It is a preview of the mapping problem
    /// the importer solves properly in M7.
    /// </remarks>
    public string? ByHeader(params string[] names)
    {
        foreach (var name in names)
        {
            var hit = Cells.FirstOrDefault(c =>
                c.Header is not null && Normalise(c.Header) == Normalise(name));

            if (hit is not null)
            {
                return hit.Value;
            }
        }

        return null;
    }

    public decimal? DecimalByHeader(params string[] names) => ParseDecimal(ByHeader(names));

    public int? IntByHeader(params string[] names)
    {
        var raw = ParseDecimal(ByHeader(names));
        return raw is null ? null : (int)Math.Round(raw.Value);
    }

    private static decimal? ParseDecimal(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? null
            : decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

    private static string Normalise(string value) =>
        new(value.ToLowerInvariant()
            .Replace('ș', 's').Replace('ş', 's')
            .Replace('ț', 't').Replace('ţ', 't')
            .Replace('ă', 'a').Replace('â', 'a').Replace('î', 'i')
            .Where(char.IsLetterOrDigit)
            .ToArray());
}

public sealed class Sheet(string sourceFile, IReadOnlyList<SheetRow> rows)
{
    public string SourceFile { get; } = sourceFile;

    public IReadOnlyList<SheetRow> Rows { get; } = rows;
}

/// <summary>
/// Reads the JSON extracted from the operator's inventory spreadsheets.
/// </summary>
public static class SheetReader
{
    public static Sheet Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var sourceFile = root.TryGetProperty("sourceFile", out var sf)
            ? sf.GetString() ?? Path.GetFileName(path)
            : Path.GetFileName(path);

        var rows = new List<SheetRow>();

        foreach (var element in root.GetProperty("units").EnumerateArray())
        {
            var sourceRow = 0;
            var cells = new List<Cell>();

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("sourceRow"))
                {
                    sourceRow = property.Value.GetInt32();
                    continue;
                }

                if (property.Value.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                var header = property.Value.TryGetProperty("header", out var h)
                    ? h.GetString()
                    : null;

                var value = property.Value.TryGetProperty("value", out var v)
                    ? v.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(value))
                {
                    cells.Add(new Cell(property.Name, header, value));
                }
            }

            rows.Add(new SheetRow(sourceRow, cells));
        }

        return new Sheet(sourceFile, rows);
    }
}
