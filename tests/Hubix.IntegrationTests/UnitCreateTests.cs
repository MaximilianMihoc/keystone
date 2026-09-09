using Hubix.Domain;
using Hubix.Domain.Common;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// Adding, renaming and removing a single apartment by hand.
/// </summary>
/// <remarks>
/// The spreadsheet importer is still how a project's inventory arrives. This is the one that was
/// missed, the code that was typed wrong, and the row that should never have existed.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class UnitCreateTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private UnitAdminService Service(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<UnitAdminService>.Instance);

    private async Task<Guid> TypeIdAsync()
    {
        await using var db = fixture.CreateContext();
        // By name, not "the first one": the catalogue is portal-wide, so another test's type
        // could otherwise be picked up here.
        return await db.UnitTypes.Where(t => t.Name == "2 camere").Select(t => t.Id).SingleAsync();
    }

    private async Task<Guid> AddAsync(string code, Guid? typeId = null)
    {
        var type = typeId ?? await TypeIdAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateAsync(
            fixture.ProjectId,
            new UnitCreate(code, type, 52.4m, 150_000m, FloorLevel: 6, Building: "F"),
            Actor, "dev", CancellationToken.None);

        return result.ShouldBeOfType<AdminWriteResult.Created>().Id;
    }

    [Fact]
    public async Task An_apartment_is_added_available_and_audited()
    {
        await fixture.ResetUnitAsync();

        var id = await AddAsync("Man.01");

        await using var verify = fixture.CreateContext();
        var unit = await verify.Units.SingleAsync(u => u.Id == id);

        unit.Code.ShouldBe("Man.01");
        unit.State.ShouldBe(UnitState.Available);
        unit.AreaUsable.ShouldBe(52.4m);

        // Computed by the database from usable plus balcony, and never writable.
        unit.AreaTotal.ShouldBe(52.4m);

        (await verify.AuditLogs.AnyAsync(a => a.Action == "unit.created" && a.EntityId == id)).ShouldBeTrue();
    }

    [Fact]
    public async Task Two_apartments_cannot_share_a_code()
    {
        await fixture.ResetUnitAsync();
        await AddAsync("Man.02");

        var type = await TypeIdAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateAsync(
            fixture.ProjectId,
            new UnitCreate("Man.02", type, 50m, 100_000m),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("Man.02");
    }

    [Fact]
    public async Task Two_admins_adding_the_same_code_at_once_get_a_refusal_not_a_crash()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        var attempts = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return await Service(db).CreateAsync(
                fixture.ProjectId,
                new UnitCreate("Man.03", type, 50m, 100_000m),
                Actor, "dev", CancellationToken.None);
        });

        var results = await Task.WhenAll(attempts);

        results.Count(r => r is AdminWriteResult.Created).ShouldBe(1);
        results.Count(r => r is AdminWriteResult.Rejected).ShouldBe(1);
    }

    [Fact]
    public async Task An_apartment_needs_the_same_things_the_importer_needs()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        await using var db = fixture.CreateContext();

        (await Service(db).CreateAsync(
            fixture.ProjectId, new UnitCreate("  ", type, 50m, 100_000m), Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("needs a code");

        (await Service(db).CreateAsync(
            fixture.ProjectId, new UnitCreate("Man.04", type, 0m, 100_000m), Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("Usable area");

        (await Service(db).CreateAsync(
            fixture.ProjectId,
            new UnitCreate("Man.05", type, 50m, 100_000m, CesiunePriceNet: 200_000m),
            Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("cesiune");
    }

    /// <summary>
    /// The catalogue is portal-wide, so a type another project introduced is usable here.
    /// This test used to assert the opposite; it is the one that pins the change.
    /// </summary>
    [Fact]
    public async Task A_type_another_project_introduced_is_accepted()
    {
        await fixture.ResetUnitAsync();

        Guid elsewhere;

        await using (var setup = fixture.CreateContext())
        {
            var project = new Project { Slug = "other-project", Name = "Other", City = "Cluj" };
            var type = new UnitType { Name = "Studio", SortOrder = 0 };
            setup.Projects.Add(project);
            setup.UnitTypes.Add(type);
            setup.Units.Add(new Unit
            {
                Project = project,
                UnitType = type,
                Code = "Ot.01",
                AreaUsable = 30m,
                BasePriceNet = 60_000m
            });

            await setup.SaveChangesAsync();
            elsewhere = type.Id;
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateAsync(
            fixture.ProjectId,
            new UnitCreate("Man.06", elsewhere, 50m, 100_000m),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Created>();
    }

    [Fact]
    public async Task A_type_that_does_not_exist_is_refused()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateAsync(
            fixture.ProjectId,
            new UnitCreate("Man.06", Guid.CreateVersion7(), 50m, 100_000m),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("does not exist");
    }

    [Fact]
    public async Task An_untouched_apartment_is_deleted()
    {
        await fixture.ResetUnitAsync();
        var id = await AddAsync("Man.07");

        await using var db = fixture.CreateContext();
        (await Service(db).DeleteAsync(id, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Deleted>();

        await using var verify = fixture.CreateContext();
        (await verify.Units.AnyAsync(u => u.Id == id)).ShouldBeFalse();
        (await verify.AuditLogs.AnyAsync(a => a.Action == "unit.deleted")).ShouldBeTrue();
    }

    [Fact]
    public async Task An_apartment_somebody_has_held_is_not_deleted()
    {
        await fixture.ResetUnitAsync();

        // Hold cascades from the unit, so deleting it would take the record of the hold with it.
        await using (var setup = fixture.CreateContext())
        {
            var prospect = new Prospect { Email = "buyer@example.invalid", FullName = "Buyer" };
            setup.Prospects.Add(prospect);
            setup.Holds.Add(new Hold
            {
                UnitId = fixture.UnitId,
                Prospect = prospect,
                AccessToken = AccessToken.New(),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(48),

                // Released, so the unit is Available again — the delete has to refuse on the
                // history rather than on the state.
                ReleasedAt = DateTimeOffset.UtcNow
            });

            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).DeleteAsync(fixture.UnitId, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("Withdraw it instead");
    }

    [Fact]
    public async Task A_mistyped_code_is_corrected()
    {
        await fixture.ResetUnitAsync();
        var id = await AddAsync("Man.O8");

        await using var db = fixture.CreateContext();
        var result = await Service(db).RenameAsync(id, "Man.08", Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Updated>();

        await using var verify = fixture.CreateContext();
        (await verify.Units.SingleAsync(u => u.Id == id)).Code.ShouldBe("Man.08");

        var audit = await verify.AuditLogs.SingleAsync(a => a.Action == "unit.renamed");
        audit.Before.ShouldNotBeNull();
        audit.Before!.ShouldContain("Man.O8");
    }

    [Fact]
    public async Task A_code_cannot_change_once_somebody_has_reserved_it()
    {
        await fixture.ResetUnitAsync();

        await using (var setup = fixture.CreateContext())
        {
            await setup.Units
                .Where(u => u.Id == fixture.UnitId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Reserved));
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).RenameAsync(fixture.UnitId, "Ap.999", Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("cannot change");
    }

    [Fact]
    public async Task A_code_cannot_be_taken_from_another_apartment()
    {
        await fixture.ResetUnitAsync();
        var id = await AddAsync("Man.10");

        await using var db = fixture.CreateContext();

        // Ap.101 is the fixture's own unit.
        var result = await Service(db).RenameAsync(id, "Ap.101", Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("already an apartment");
    }
}
