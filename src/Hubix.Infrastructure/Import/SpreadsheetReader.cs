using ClosedXML.Excel;

namespace Hubix.Infrastructure.Import;

/// <summary>One column of the uploaded sheet.</summary>
/// <param name="Letter">Excel column letter, so a person can find it in their own file.</param>
/// <param name="Header">The header text, or null where the cell was empty.</param>
/// <param name="LooksLikeData">
/// The header reads like a row of the table rather than a name for one. Cartierul Vienez has
/// exactly this: a data row was pasted over cell A1, so its first column is headed
/// "Etaj 5 - M12_39A - Studio". Flagged rather than guessed at, because the fix is a person
/// choosing what that column is.
/// </param>
/// <param name="Sample">First few non-empty values, so the mapping screen can show the column.</param>
public sealed record SheetColumn(
    string Letter,
    string? Header,
    bool LooksLikeData,
    IReadOnlyList<string> Sample);

/// <summary>One row of values, addressed by column letter.</summary>
public sealed class SheetRow(int number, IReadOnlyDictionary<string, string> values)
{
    /// <summary>Row number in the file, so an error can name where to look.</summary>
    public int Number { get; } = number;

    public IReadOnlyDictionary<string, string> Values { get; } = values;

    public string? this[string letter] =>
        Values.TryGetValue(letter, out var value) ? value : null;

    public bool IsEmpty => Values.Count == 0;
}

/// <summary>The sheet as read: where its header is, what its columns are, and its rows.</summary>
public sealed record ParsedSheet(
    string WorksheetName,
    int HeaderRow,
    IReadOnlyList<SheetColumn> Columns,
    IReadOnlyList<SheetRow> Rows);

/// <summary>
/// Reads a Hubix inventory workbook.
/// </summary>
/// <remarks>
/// Assumes nothing about layout. The five sheets this has to accept disagree on column order,
/// column names, and even whether the header row survived, so everything here is detected and
/// then shown to a person for confirmation.
/// </remarks>
public static class SpreadsheetReader
{
    /// <summary>How far down to look for a header row before giving up.</summary>
    private const int SearchDepth = 10;

    /// <summary>Values shown per column on the mapping screen.</summary>
    private const int SampleSize = 3;

    public static ParsedSheet Read(Stream stream, string? worksheetName = null)
    {
        using var workbook = new XLWorkbook(stream);

        var sheet = worksheetName is null
            ? workbook.Worksheets.First()
            : workbook.Worksheets.First(w => w.Name == worksheetName);

        var used = sheet.RangeUsed();

        if (used is null)
        {
            return new ParsedSheet(sheet.Name, 0, [], []);
        }

        var firstRow = used.FirstRow().RowNumber();
        var lastRow = used.LastRow().RowNumber();
        var letters = used.Columns().Select(c => c.ColumnLetter()).ToList();

        var headerRow = FindHeaderRow(sheet, letters, firstRow, lastRow);

        var rows = new List<SheetRow>();

        for (var number = headerRow + 1; number <= lastRow; number++)
        {
            var values = new Dictionary<string, string>();

            foreach (var letter in letters)
            {
                var text = Text(sheet, letter, number);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    values[letter] = text;
                }
            }

            // Blank rows are structure, not data: the sheets use them as separators.
            if (values.Count > 0)
            {
                rows.Add(new SheetRow(number, values));
            }
        }

        var columns = letters.Select(letter =>
        {
            var header = Text(sheet, letter, headerRow);

            var sample = rows
                .Select(r => r[letter])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Take(SampleSize)
                .Select(v => v!)
                .ToList();

            return new SheetColumn(
                letter,
                string.IsNullOrWhiteSpace(header) ? null : header,
                LooksLikeData: header is not null && HeaderText.ReadsLikeData(header),
                sample);
        }).ToList();

        return new ParsedSheet(sheet.Name, headerRow, columns, rows);
    }

    /// <summary>Worksheet names, so somebody can pick when a workbook has several.</summary>
    public static IReadOnlyList<string> WorksheetNames(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        return workbook.Worksheets.Select(w => w.Name).ToList();
    }

    /// <summary>
    /// The row that best reads like a header.
    /// </summary>
    /// <remarks>
    /// Scored rather than assumed to be the first row: these workbooks carry title rows, logos
    /// and merged banners above the table. The score is how many cells match a known column
    /// name, which is a far better signal than "is row 1" — and it survives one corrupted cell,
    /// which is the difference between reading Cartierul Vienez and refusing it.
    /// </remarks>
    private static int FindHeaderRow(
        IXLWorksheet sheet,
        IReadOnlyList<string> letters,
        int firstRow,
        int lastRow)
    {
        var best = firstRow;
        var bestScore = -1;

        for (var number = firstRow; number <= Math.Min(firstRow + SearchDepth, lastRow); number++)
        {
            var score = letters.Count(letter =>
                ImportField.Guess(Text(sheet, letter, number)) is not null);

            if (score > bestScore)
            {
                bestScore = score;
                best = number;
            }
        }

        return best;
    }

    /// <summary>
    /// The cell as text, formatted the way the sheet shows it.
    /// </summary>
    /// <remarks>
    /// Formatted rather than raw, so a date reads as a date and not as 45,231 — but numbers
    /// are taken raw, because a price shown as "135.776" must not arrive as a thousand
    /// separator problem later.
    /// </remarks>
    private static string Text(IXLWorksheet sheet, string letter, int row)
    {
        var cell = sheet.Cell(row, letter);

        if (cell.IsEmpty())
        {
            return string.Empty;
        }

        return cell.DataType switch
        {
            XLDataType.Number => cell.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd"),
            XLDataType.Boolean => cell.GetBoolean() ? "DA" : "NU",
            _ => cell.GetFormattedString().Trim()
        };
    }
}
