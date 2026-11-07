using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// Apartment types and floors: how a project's inventory is organised.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ProjectStructureTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private ProjectAdminService Service(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<ProjectAdminService>.Instance);

    private async Task<Guid> SeededTypeAsync()
    {
        await using var db = fixture.CreateContext();
        // By name, not "the first one": the catalogue is portal-wide, so another test's type
        // could otherwise be picked up here.
        return await db.UnitTypes.Where(t => t.Name == "2 camere").Select(t => t.Id).SingleAsync();
    }

    [Fact]
    public async Task A_type_gets_a_name_a_visitor_can_read()
    {
        await fixture.ResetUnitAsync();
        var typeId = await SeededTypeAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditUnitTypeAsync(
            typeId,
            new UnitTypeEdit(3, new Dictionary<string, string> { [Cultures.English] = "Two rooms" }),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Updated>();

        await using var verify = fixture.CreateContext();
        var type = await verify.UnitTypes
            .Include(t => t.Translations)
            .SingleAsync(t => t.Id == typeId);

        type.SortOrder.ShouldBe(3);
        type.DisplayName(Cultures.English).ShouldBe("Two rooms");

        // The canonical name is the importer's key and is not editable, so it must be untouched.
        type.Name.ShouldBe("2 camere");

        // Romanian has no translation row, so it falls back to the canonical name rather than
        // to the English one.
        type.DisplayName(Cultures.Romanian).ShouldBe("2 camere");
    }

    [Fact]
    public async Task Clearing_a_display_name_removes_the_translation_rather_than_blanking_it()
    {
        await fixture.ResetUnitAsync();
        var typeId = await SeededTypeAsync();

        await using (var set = fixture.CreateContext())
        {
            await Service(set).EditUnitTypeAsync(
                typeId, new UnitTypeEdit(0, new Dictionary<string, string> { [Cultures.English] = "Two rooms" }),
                Actor, "dev", CancellationToken.None);
        }

        await using (var clear = fixture.CreateContext())
        {
            await Service(clear).EditUnitTypeAsync(
                typeId, new UnitTypeEdit(0, new Dictionary<string, string> { [Cultures.English] = "  " }),
                Actor, "dev", CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();

        // DisplayName is required on the row, so "no translation" is the row not existing.
        (await verify.UnitTypeTranslations.AnyAsync(t => t.UnitTypeId == typeId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_type_with_apartments_is_not_removed()
    {
        await fixture.ResetUnitAsync();
        var typeId = await SeededTypeAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).DeleteUnitTypeAsync(typeId, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("2 camere");

        await using var verify = fixture.CreateContext();
        (await verify.UnitTypes.AnyAsync(t => t.Id == typeId)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_empty_type_is_removed()
    {
        await fixture.ResetUnitAsync();

        Guid typeId;

        await using (var setup = fixture.CreateContext())
        {
            var type = new UnitType { Name = "Penthouse", SortOrder = 9 };
            setup.UnitTypes.Add(type);
            await setup.SaveChangesAsync();
            typeId = type.Id;
        }

        await using var db = fixture.CreateContext();
        (await Service(db).DeleteUnitTypeAsync(typeId, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Deleted>();

        await using var verify = fixture.CreateContext();
        (await verify.UnitTypes.AnyAsync(t => t.Id == typeId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_floor_is_created_and_finally_gets_a_label()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateFloorAsync(
            fixture.ProjectId, new FloorEdit("F", 1, "Etaj 1"), Actor, "dev", CancellationToken.None);

        var id = result.ShouldBeOfType<AdminWriteResult.Created>().Id;

        await using var verify = fixture.CreateContext();
        var floor = await verify.Floors.SingleAsync(f => f.Id == id);

        floor.Building.ShouldBe("F");
        floor.Level.ShouldBe(1);
        floor.Label.ShouldBe("Etaj 1");
    }

    [Fact]
    public async Task Two_floors_cannot_claim_the_same_level_of_the_same_block()
    {
        await fixture.ResetUnitAsync();

        await using (var first = fixture.CreateContext())
        {
            await Service(first).CreateFloorAsync(
                fixture.ProjectId, new FloorEdit("F", 2, "Etaj 2"), Actor, "dev", CancellationToken.None);
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateFloorAsync(
            fixture.ProjectId, new FloorEdit("F", 2, "Second"), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("already a floor 2 in F");
    }

    [Fact]
    public async Task Two_floors_cannot_claim_the_same_level_when_there_is_no_block()
    {
        await fixture.ResetUnitAsync();

        await using (var first = fixture.CreateContext())
        {
            await Service(first).CreateFloorAsync(
                fixture.ProjectId, new FloorEdit(null, 3, "Etaj 3"), Actor, "dev", CancellationToken.None);
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateFloorAsync(
            fixture.ProjectId, new FloorEdit(null, 3, null), Actor, "dev", CancellationToken.None);

        // UX_Floors_OnePerLevel removes EF's default "Building IS NOT NULL" filter on purpose, so
        // a single-building project is protected too. The guard has to agree with the index.
        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("already a floor 3");
    }

    [Fact]
    public async Task A_floor_can_be_renamed_and_moved()
    {
        await fixture.ResetUnitAsync();

        Guid id;

        await using (var create = fixture.CreateContext())
        {
            var created = await Service(create).CreateFloorAsync(
                fixture.ProjectId, new FloorEdit("F", 4, "Etaj 4"), Actor, "dev", CancellationToken.None);

            id = created.ShouldBeOfType<AdminWriteResult.Created>().Id;
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditFloorAsync(
            id, new FloorEdit("E", 5, "Etaj 5"), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Updated>().Changes.Keys
            .ShouldBe(["Building", "Level", "Label"], ignoreOrder: true);
    }

    [Fact]
    public async Task Deleting_a_floor_leaves_its_apartments_alone()
    {
        await fixture.ResetUnitAsync();

        Guid id;

        await using (var create = fixture.CreateContext())
        {
            // The fixture's unit sits on level 1, and floors join by building and level rather
            // than by key — so removing the floor row must not touch it.
            var created = await Service(create).CreateFloorAsync(
                fixture.ProjectId, new FloorEdit(null, 1, "Etaj 1"), Actor, "dev", CancellationToken.None);

            id = created.ShouldBeOfType<AdminWriteResult.Created>().Id;
        }

        await using var db = fixture.CreateContext();
        (await Service(db).DeleteFloorAsync(id, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Deleted>();

        await using var verify = fixture.CreateContext();
        (await verify.Units.AnyAsync(u => u.Id == fixture.UnitId)).ShouldBeTrue();
        (await verify.Floors.AnyAsync(f => f.Id == id)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_type_can_be_added_by_hand_and_not_twice()
    {
        Guid id;

        await using (var create = fixture.CreateContext())
        {
            // Typed-in inventory has to start somewhere: without this the add-an-apartment form
            // has no type to choose, and only an import could create one.
            var created = await Service(create).CreateUnitTypeAsync(
                "  3 camere  ", 7, Actor, "dev", CancellationToken.None);

            id = created.ShouldBeOfType<AdminWriteResult.Created>().Id;
        }

        await using (var verify = fixture.CreateContext())
        {
            var type = await verify.UnitTypes.SingleAsync(t => t.Id == id);
            type.Name.ShouldBe("3 camere");
            type.SortOrder.ShouldBe(7);
        }

        // The name is the importer's match key, so the catalogue holds one of each.
        await using var again = fixture.CreateContext();
        var second = await Service(again).CreateUnitTypeAsync(
            "3 camere", 8, Actor, "dev", CancellationToken.None);

        second.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("already");
    }
}
