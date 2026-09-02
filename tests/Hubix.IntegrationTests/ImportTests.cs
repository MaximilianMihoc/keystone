using ClosedXML.Excel;
using Hubix.Domain;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// The importer against a real database.
/// </summary>
/// <remarks>
/// The rule under test above all others: a file may never change a unit somebody is part-way
/// through buying. Everything else here is convenience; that one is the reason the dry run
/// exists at all.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class ImportTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static readonly string[] Headers =
    [
        "Nr Crt", "Disponibil?", "Bloc", "Etaj", "Cod Apartament", "Tip Apartament",
        "Orientare", "MPU", "MP Balcon/Logie", "Pret total apartament fara TVA",
        "Pret cesiune faraTVA", "Comentarii"
    ];

    /// <summary>
    /// The fixture's own unit, as the sheet describes it — matching the seed exactly, so that
    /// "unchanged" means unchanged and any difference in a test is the one that test is about.
    /// </summary>
    private static string[] FixtureRow(
        string code = "Ap.101",
        string available = "DA",
        string area = "45.71",
        string price = "135776",
        string type = "2 camere",
        string notes = "") =>
        ["", available, "", "1", code, type, "", area, "4.64", price, "14444.62", notes];

    private static MemoryStream Sheet(params string[][] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Apartamente");

        for (var i = 0; i < Headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = Headers[i];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c].Length > 0)
                {
                    sheet.Cell(r + 2, c + 1).Value = rows[r][c];
                }
            }
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>The mapping the screen would auto-fill from these headers.</summary>
    private static ImportMapping MappingFor(ParsedSheet sheet)
    {
        var columns = new Dictionary<string, string>();

        foreach (var column in sheet.Columns)
        {
            if (ImportField.Guess(column.Header) is { } field && !columns.ContainsKey(field.Field))
            {
                columns[field.Field] = column.Letter;
            }
        }

        return new ImportMapping(columns);
    }

    private async Task<ImportPlan> PlanAsync(MemoryStream stream)
    {
        await using var db = fixture.CreateContext();

        var sheet = SpreadsheetReader.Read(stream);

        return await new ImportPlanner(db).PlanAsync(
            fixture.ProjectId, sheet, MappingFor(sheet),
            ImportPlanner.Hash(stream), CancellationToken.None);
    }

    private async Task<ImportResult> CommitAsync(ImportPlan plan)
    {
        await using var db = fixture.CreateContext();

        return await new ImportService(db, TimeProvider.System, NullLogger<ImportService>.Instance)
            .CommitAsync(fixture.ProjectId, plan, Actor, "dev@hubix.invalid", CancellationToken.None);
    }

    // --- The rule --------------------------------------------------------------------

    [Theory]
    [InlineData(UnitState.Held)]
    [InlineData(UnitState.Reserved)]
    [InlineData(UnitState.Contracted)]
    [InlineData(UnitState.Sold)]
    public async Task A_file_cannot_touch_a_unit_somebody_is_buying(UnitState state)
    {
        await fixture.ResetUnitAsync();

        await using (var db = fixture.CreateContext())
        {
            await db.Units.Where(u => u.Id == fixture.UnitId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, state));
        }

        using var stream = Sheet(FixtureRow(price: "999999", area: "1"));
        var plan = await PlanAsync(stream);

        var row = plan.Rows.Single();
        row.Action.ShouldBe(RowAction.Skipped);
        row.Reason.ShouldContain(state.ToString().ToLowerInvariant());
        row.Changes.ShouldBeEmpty();

        // And nothing would be written even if somebody forced the commit.
        await CommitAsync(plan);

        await using var verify = fixture.CreateContext();
        var unit = await verify.Units.AsNoTracking().SingleAsync(u => u.Id == fixture.UnitId);
        unit.BasePriceNet.ShouldBe(DatabaseFixture.SeedPrice.Price);
        unit.AreaUsable.ShouldBe(DatabaseFixture.SeedPrice.AreaUsable);
    }

    [Fact]
    public async Task A_unit_taken_while_the_plan_was_being_read_is_left_alone()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow(price: "150000"));
        var plan = await PlanAsync(stream);

        plan.Rows.Single().Action.ShouldBe(RowAction.Update);

        // Somebody clicks Reserve while the diff is on screen. The check in the planner has
        // already passed; the one at commit is what actually protects them.
        await using (var db = fixture.CreateContext())
        {
            await db.Units.Where(u => u.Id == fixture.UnitId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Held));
        }

        var result = await CommitAsync(plan);

        result.Updated.ShouldBe(0);
        result.Skipped.ShouldBe(1);
        result.Notes.ShouldContain(n => n.Contains("while this was being reviewed"));

        await using var verify = fixture.CreateContext();
        (await verify.Units.AsNoTracking().SingleAsync(u => u.Id == fixture.UnitId))
            .BasePriceNet.ShouldBe(DatabaseFixture.SeedPrice.Price);
    }

    [Fact]
    public async Task A_withdrawn_unit_may_still_be_corrected()
    {
        await fixture.ResetUnitAsync();

        await using (var db = fixture.CreateContext())
        {
            await db.Units.Where(u => u.Id == fixture.UnitId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Withdrawn));
        }

        // Off sale is not sold. Nobody is relying on its price.
        using var stream = Sheet(FixtureRow(price: "140000"));
        var plan = await PlanAsync(stream);

        plan.Rows.Single().Action.ShouldBe(RowAction.Update);
    }

    // --- Reading a file --------------------------------------------------------------

    [Fact]
    public async Task An_unchanged_file_proposes_nothing()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow());
        var plan = await PlanAsync(stream);

        // Re-importing the same sheet is the common case and should be visibly quiet.
        plan.Rows.Single().Action.ShouldBe(RowAction.Unchanged);
        plan.CanCommit.ShouldBeFalse();
    }

    [Fact]
    public async Task A_changed_price_is_shown_before_and_after()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow(price: "141000"));
        var plan = await PlanAsync(stream);

        var change = plan.Rows.Single().Changes[ImportField.BasePriceNet];
        change.Before.ShouldBe(DatabaseFixture.SeedPrice.Price);
        change.After.ShouldBe(141_000m);
    }

    [Fact]
    public async Task A_new_apartment_is_created_and_an_unknown_type_is_listed_first()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow(), FixtureRow(code: "Ap.102", type: "3 camere"));
        var plan = await PlanAsync(stream);

        plan.Creates.ShouldBe(1);

        // An unfamiliar type is usually a typo, so it is named before anything is written.
        plan.NewTypes.ShouldBe(["3 camere"]);

        var result = await CommitAsync(plan);
        result.Created.ShouldBe(1);

        await using var verify = fixture.CreateContext();
        var created = await verify.Units.AsNoTracking()
            .Include(u => u.UnitType)
            .SingleAsync(u => u.Code == "Ap.102");

        created.UnitType!.Name.ShouldBe("3 camere");
        created.BasePriceNet.ShouldBe(135_776m);
        created.State.ShouldBe(UnitState.Available);
    }

    [Fact]
    public async Task A_new_apartment_the_sheet_calls_taken_starts_reserved()
    {
        await fixture.ResetUnitAsync();

        // On a first import the sheet is all there is, so it may seed a new unit's state.
        using var stream = Sheet(FixtureRow(code: "Ap.103", available: "NU"));
        var plan = await PlanAsync(stream);

        await CommitAsync(plan);

        await using var verify = fixture.CreateContext();
        (await verify.Units.AsNoTracking().SingleAsync(u => u.Code == "Ap.103"))
            .State.ShouldBe(UnitState.Reserved);
    }

    [Fact]
    public async Task A_stale_availability_column_is_reported_and_never_applied()
    {
        await fixture.ResetUnitAsync();

        // The sheet was saved on Tuesday and says taken; here the unit is available.
        using var stream = Sheet(FixtureRow(available: "NU"));
        var plan = await PlanAsync(stream);

        var row = plan.Rows.Single();
        row.Action.ShouldBe(RowAction.Unchanged);
        row.StaleNote.ShouldNotBeNull();
        row.StaleNote.ShouldContain("here it is available");

        await using var verify = fixture.CreateContext();
        (await verify.Units.AsNoTracking().SingleAsync(u => u.Id == fixture.UnitId))
            .State.ShouldBe(UnitState.Available);
    }

    [Fact]
    public async Task A_unit_missing_from_the_file_is_reported_and_never_deleted()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow(code: "Ap.999"));
        var plan = await PlanAsync(stream);

        // Deleting inventory because a column was filtered when the file was saved would be
        // unrecoverable. It is information, not an action.
        plan.NotInFile.ShouldContain(u => u.Code == "Ap.101");

        await CommitAsync(plan);

        await using var verify = fixture.CreateContext();
        (await verify.Units.AsNoTracking().AnyAsync(u => u.Id == fixture.UnitId)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_same_apartment_twice_in_one_file_is_refused()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow(price: "1"), FixtureRow(price: "2"));
        var plan = await PlanAsync(stream);

        // The file disagrees with itself; picking one would be a coin toss.
        plan.Failed.ShouldBe(1);
        plan.CanCommit.ShouldBeFalse();
    }

    [Fact]
    public async Task A_row_with_no_code_fails_rather_than_creating_a_nameless_unit()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow(code: ""));
        var plan = await PlanAsync(stream);

        plan.Failed.ShouldBe(1);
        plan.Rows.Single().Reason.ShouldContain("code");
    }

    [Fact]
    public async Task A_price_that_is_not_a_number_names_the_column_and_the_value()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow(price: "de discutat"));
        var plan = await PlanAsync(stream);

        plan.Rows.Single().Action.ShouldBe(RowAction.Failed);
        plan.Rows.Single().Reason.ShouldContain("de discutat");
        plan.CanCommit.ShouldBeFalse();
    }

    [Fact]
    public async Task A_mapping_without_the_price_column_refuses_to_plan_at_all()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow());
        var sheet = SpreadsheetReader.Read(stream);

        var partial = MappingFor(sheet).Columns
            .Where(c => c.Key != ImportField.BasePriceNet)
            .ToDictionary(c => c.Key, c => c.Value);

        await using var db = fixture.CreateContext();
        var plan = await new ImportPlanner(db).PlanAsync(
            fixture.ProjectId, sheet, new ImportMapping(partial), "hash", CancellationToken.None);

        plan.Problems.ShouldContain(p => p.Contains("Price excl. VAT"));
        plan.CanCommit.ShouldBeFalse();
    }

    [Fact]
    public async Task An_import_writes_one_audit_row_naming_what_it_touched()
    {
        await fixture.ResetUnitAsync();

        using var stream = Sheet(FixtureRow(price: "138000"));
        await CommitAsync(await PlanAsync(stream));

        await using var db = fixture.CreateContext();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "units.imported");

        audit.ActorLabel.ShouldBe("dev@hubix.invalid");
        audit.After.ShouldContain("Ap.101");
    }

    // --- Export ----------------------------------------------------------------------

    [Fact]
    public async Task What_is_exported_can_be_imported_again_unchanged()
    {
        await fixture.ResetUnitAsync();

        byte[] file;
        await using (var db = fixture.CreateContext())
        {
            file = await new InventoryExporter(db).ExportAsync(fixture.ProjectId, CancellationToken.None);
        }

        using var stream = new MemoryStream(file);
        var plan = await PlanAsync(stream);

        // The round trip is the test of both halves at once: if the exporter writes a column
        // the importer cannot read back, this is where it shows.
        plan.Rows.Count.ShouldBe(1);
        plan.Rows.Single().Action.ShouldBe(RowAction.Unchanged);
        plan.Problems.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_export_uses_Hubix_own_column_names_and_calculates_the_tranches()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var file = await new InventoryExporter(db).ExportAsync(fixture.ProjectId, CancellationToken.None);

        using var workbook = new XLWorkbook(new MemoryStream(file));
        var sheet = workbook.Worksheet(1);

        var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();

        headers.ShouldContain("Cod Apartament");
        headers.ShouldContain("Pret cesiune faraTVA");

        // Their names for the payments, not ours.
        headers.ShouldContain("Transa 1 apartament");
        headers.ShouldContain("Transa 2 apartament");

        var transa1 = headers.IndexOf("Transa 1 apartament") + 1;
        sheet.Cell(2, transa1).GetDouble().ShouldBeGreaterThan(0);
    }
}

public class NumberParsingTests
{
    [Theory]
    [InlineData("135776", 135776)]
    [InlineData("135.776", 135776)]      // Romanian thousands
    [InlineData("135,776", 135776)]      // English thousands
    [InlineData("1.234,56", 1234.56)]    // Romanian
    [InlineData("1,234.56", 1234.56)]    // English
    [InlineData("45.71", 45.71)]
    [InlineData("45,71", 45.71)]
    [InlineData(" 14 444.62 ", 14444.62)]
    [InlineData("135776 €", 135776)]
    public void Numbers_survive_being_typed_in_either_locale(string raw, double expected)
    {
        // These files are edited by people in two locales and mailed between them.
        ImportPlanner.ParseNumber(raw).ShouldBe((decimal)expected);
    }

    [Theory]
    [InlineData("de discutat")]
    [InlineData("-")]
    [InlineData("")]
    public void Anything_that_is_not_a_number_is_refused_rather_than_guessed_at(string raw)
    {
        ImportPlanner.ParseNumber(raw).ShouldBeNull();
    }

    [Theory]
    [InlineData("DA", true)]
    [InlineData("da", true)]
    [InlineData("NU", false)]
    [InlineData("", false)]
    [InlineData("poate", false)]
    public void Hubix_writes_DA_and_NU(string raw, bool expected)
    {
        ImportPlanner.ParseYesNo(raw).ShouldBe(expected);
    }
}
