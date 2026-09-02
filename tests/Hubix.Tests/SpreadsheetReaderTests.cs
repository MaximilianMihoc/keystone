using ClosedXML.Excel;
using Hubix.Infrastructure.Import;
using Shouldly;

namespace Hubix.Tests;

/// <summary>
/// Reading Hubix's inventory workbooks.
/// </summary>
/// <remarks>
/// The workbooks are built here rather than checked in as fixtures. The real files carry live
/// prices and buyer names and stay out of this repository, and a written fixture states the
/// defect it is testing — "cell A1 holds a data row" — where a binary would only contain it.
/// </remarks>
public class SpreadsheetReaderTests
{
    /// <summary>Roka's header row, in its own order and spelling.</summary>
    private static readonly string[] RokaHeaders =
    [
        "Nr Crt", "Disponibil?", "Bloc", "Scara", "Etaj", "Cod Apartament", "Tip Apartament",
        "Orientare", "MPC", "MPU", "MP Balcon/Logie", "Pret total apartament fara TVA",
        "Pret cesiune faraTVA", "Comentarii"
    ];

    private static readonly string[] RokaRow =
    [
        "1", "DA", "F", "1", "1", "Ap.104", "2 camere", "S-W", "50.35", "45.71", "4.64",
        "135776", "14444.62", ""
    ];

    private static MemoryStream Workbook(Action<IXLWorksheet> build)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Apartamente");
        build(sheet);

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static void WriteRow(IXLWorksheet sheet, int row, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Length > 0)
            {
                sheet.Cell(row, i + 1).Value = values[i];
            }
        }
    }

    private static MemoryStream RokaLike(int titleRows = 0)
    {
        return Workbook(sheet =>
        {
            for (var i = 1; i <= titleRows; i++)
            {
                sheet.Cell(i, 1).Value = "HUBIX — Roka Park View";
            }

            WriteRow(sheet, titleRows + 1, RokaHeaders);
            WriteRow(sheet, titleRows + 2, RokaRow);
        });
    }

    [Fact]
    public void A_plain_sheet_reads_its_header_and_rows()
    {
        using var stream = RokaLike();
        var sheet = SpreadsheetReader.Read(stream);

        sheet.HeaderRow.ShouldBe(1);
        sheet.Rows.Count.ShouldBe(1);
        sheet.Columns.Count.ShouldBe(RokaHeaders.Length);
        sheet.Rows[0]["F"].ShouldBe("Ap.104");
    }

    [Fact]
    public void A_banner_above_the_table_does_not_become_the_header()
    {
        // These workbooks are shared with buyers, so they carry title rows and logos.
        using var stream = RokaLike(titleRows: 3);
        var sheet = SpreadsheetReader.Read(stream);

        sheet.HeaderRow.ShouldBe(4);
        sheet.Rows.Count.ShouldBe(1);
        sheet.Rows[0]["F"].ShouldBe("Ap.104");
    }

    [Fact]
    public void Blank_separator_rows_are_not_read_as_apartments()
    {
        using var stream = Workbook(sheet =>
        {
            WriteRow(sheet, 1, RokaHeaders);
            WriteRow(sheet, 2, RokaRow);
            // row 3 left blank, as the sheets use it
            WriteRow(sheet, 4, RokaRow);
        });

        SpreadsheetReader.Read(stream).Rows.Count.ShouldBe(2);
    }

    [Fact]
    public void A_data_row_pasted_over_a_header_cell_is_flagged_not_trusted()
    {
        // Cartierul Vienez, exactly: every header intact except A1, which holds a record.
        using var stream = Workbook(sheet =>
        {
            WriteRow(sheet, 1, RokaHeaders);
            sheet.Cell(1, 1).Value = "Etaj 5 - M12_39A - Studio";
            WriteRow(sheet, 2, RokaRow);
        });

        var sheet = SpreadsheetReader.Read(stream);

        // The header row is still row 1. One ruined cell must not push detection down onto
        // the data, which would lose a row and mis-name every column.
        sheet.HeaderRow.ShouldBe(1);
        sheet.Rows.Count.ShouldBe(1);

        var first = sheet.Columns[0];
        first.Header.ShouldBe("Etaj 5 - M12_39A - Studio");
        first.LooksLikeData.ShouldBeTrue();

        // And it is not silently mapped to anything.
        ImportField.Guess(first.Header).ShouldBeNull();

        // The columns either side are unaffected.
        sheet.Columns[1].LooksLikeData.ShouldBeFalse();
        ImportField.Guess(sheet.Columns[5].Header)!.Field.ShouldBe(ImportField.Code);
    }

    [Fact]
    public void Columns_carry_a_sample_so_a_person_can_see_what_they_are_mapping()
    {
        using var stream = RokaLike();
        var sheet = SpreadsheetReader.Read(stream);

        sheet.Columns.Single(c => c.Letter == "F").Sample.ShouldBe(["Ap.104"]);
    }

    [Fact]
    public void Numbers_are_read_as_numbers_not_as_the_sheets_formatting_of_them()
    {
        using var stream = Workbook(sheet =>
        {
            WriteRow(sheet, 1, RokaHeaders);
            sheet.Cell(2, 6).Value = "Ap.104";

            // Formatted with a thousand separator, as the real sheets are.
            sheet.Cell(2, 12).Value = 135776;
            sheet.Cell(2, 12).Style.NumberFormat.Format = "#,##0";
        });

        var sheet = SpreadsheetReader.Read(stream);

        // "135,776" would be unparseable later, or worse, parseable as 135.776.
        sheet.Rows[0]["L"].ShouldBe("135776");
    }

    [Fact]
    public void An_empty_workbook_is_read_rather_than_thrown_at()
    {
        using var stream = Workbook(_ => { });
        var sheet = SpreadsheetReader.Read(stream);

        sheet.Rows.ShouldBeEmpty();
        sheet.Columns.ShouldBeEmpty();
    }
}

public class ImportFieldTests
{
    [Theory]
    [InlineData("Cod Apartament", ImportField.Code)]
    [InlineData("Cod A", ImportField.Code)]
    [InlineData("Tip Apartament", ImportField.TypeName)]
    [InlineData("MPU", ImportField.AreaUsable)]
    [InlineData("MPC", ImportField.AreaBuilt)]
    [InlineData("Disponibil?", ImportField.Available)]
    [InlineData("Pret total apartament fara TVA", ImportField.BasePriceNet)]
    public void Known_headers_are_recognised(string header, string expected)
    {
        ImportField.Guess(header)!.Field.ShouldBe(expected);
    }

    [Theory]
    // The same column, written differently in different projects.
    [InlineData("MP Balcon/Logie")]
    [InlineData("MPU Balcon")]
    [InlineData("MP Balcon")]
    public void The_balcony_column_is_recognised_however_it_was_written(string header)
    {
        ImportField.Guess(header)!.Field.ShouldBe(ImportField.AreaBalcony);
    }

    [Fact]
    public void A_rate_suffix_does_not_hide_the_column()
    {
        // Cartierul Vienez heads this "Pret cesiune faraTVA 21%".
        ImportField.Guess("Pret cesiune faraTVA 21%")!.Field.ShouldBe(ImportField.CesiunePriceNet);
        ImportField.Guess("Pret cesiune faraTVA")!.Field.ShouldBe(ImportField.CesiunePriceNet);
    }

    [Fact]
    public void Accents_and_spelling_do_not_matter()
    {
        ImportField.Guess("Preț cesiune fără TVA")!.Field.ShouldBe(ImportField.CesiunePriceNet);
        ImportField.Guess("suprafata utila")!.Field.ShouldBe(ImportField.AreaUsable);
    }

    [Fact]
    public void A_column_nobody_recognises_is_left_for_a_person_to_decide()
    {
        ImportField.Guess("Comments Istratescu").ShouldBeNull();
        ImportField.Guess("Indentificator").ShouldBeNull();
        ImportField.Guess("").ShouldBeNull();
        ImportField.Guess(null).ShouldBeNull();
    }

    [Fact]
    public void Nothing_in_the_mapping_can_write_a_state_or_a_tranche()
    {
        var keys = ImportField.All.Select(f => f.Field).ToList();

        // A spreadsheet must not be able to mark a unit sold, and tranche amounts are
        // calculated from the pricing rule. Both were columns in the sheets this replaces.
        keys.ShouldNotContain("state");
        keys.ShouldNotContain("transa1");

        ImportField.Guess("Transa 1 apartament").ShouldBeNull();
        ImportField.Guess("Transa 3 apartament").ShouldBeNull();
    }
}
