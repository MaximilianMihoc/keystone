using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// Parking writes, and the audit rows they were missing.
/// </summary>
/// <remarks>
/// These endpoints used to write straight to the context with no audit row — the only such paths
/// in the codebase. A bay carries a state and gets sold, and its price is what a reservation
/// snapshots at the moment of sale, so the questions this makes answerable are real ones.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class ParkingAdminTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private ParkingAdminService Service(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<ParkingAdminService>.Instance);

    private async Task<SpacesAdded> AddAsync(string prefix, int from, int to, int level = -1)
    {
        await using var db = fixture.CreateContext();

        var result = await Service(db).AddSpacesAsync(
            fixture.ProjectId, new SpaceRange(prefix, from, to),
            ParkingSpaceKind.Underground, level, Actor, "dev", CancellationToken.None);

        return (SpacesAdded)result.ShouldBeOfType<AdminWriteResult.Payload>().Value;
    }

    private async Task<AuditLog?> LastAuditAsync(string action)
    {
        await using var db = fixture.CreateContext();

        return await db.AuditLogs
            .Where(a => a.Action == action)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();
    }

    [Fact]
    public async Task Adding_a_range_pads_the_codes_and_writes_one_audit_row()
    {
        await fixture.ResetUnitAsync();

        var added = await AddAsync("A", 1, 10);

        added.Created.ShouldBe(10);
        added.Skipped.ShouldBeEmpty();

        await using var db = fixture.CreateContext();
        var codes = await db.ParkingSpaces
            .Where(s => s.ProjectId == fixture.ProjectId)
            .Select(s => s.Code)
            .ToListAsync();

        // Padded to the width of the largest number, so these sort as text the way the signage
        // numbers them.
        codes.ShouldContain("A01");
        codes.ShouldContain("A10");

        // One row for the range, not ten. The codes are in the payload.
        var audit = await LastAuditAsync("parking.spaces.added");
        audit.ShouldNotBeNull();
        audit!.EntityId.ShouldBe(fixture.ProjectId);
        audit.After.ShouldContain("A01");
        audit.ActorLabel.ShouldBe("dev");
    }

    [Fact]
    public async Task Extending_a_range_skips_what_is_already_there()
    {
        await fixture.ResetUnitAsync();
        await AddAsync("A", 1, 5);

        var added = await AddAsync("A", 1, 9);

        // Extending a car park should not be refused because its first bays exist.
        added.Created.ShouldBe(4);
        added.Skipped.Count.ShouldBe(5);
    }

    [Fact]
    public async Task A_range_that_is_entirely_taken_is_refused()
    {
        await fixture.ResetUnitAsync();
        await AddAsync("A", 1, 5);

        await using var db = fixture.CreateContext();
        var result = await Service(db).AddSpacesAsync(
            fixture.ProjectId, new SpaceRange("A", 1, 5),
            ParkingSpaceKind.Underground, -1, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("already exists");
    }

    [Fact]
    public async Task Editing_a_space_is_audited_with_what_changed()
    {
        await fixture.ResetUnitAsync();
        await AddAsync("A", 1, 3);

        Guid spaceId;

        await using (var find = fixture.CreateContext())
        {
            spaceId = await find.ParkingSpaces.Where(s => s.Code == "A2").Select(s => s.Id).FirstAsync();
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditSpaceAsync(
            spaceId,
            new SpaceEdit("B2", ParkingSpaceKind.AboveGroundCovered, 1, "Near the lift"),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Updated>().Changes.Keys
            .ShouldBe(["Code", "Kind", "Level", "AdminNotes"], ignoreOrder: true);

        var audit = await LastAuditAsync("parking.space.edited");
        audit.ShouldNotBeNull();

        // The kind is recorded by name. As an ordinal it would say "1", and the numbers move if
        // the enum ever gains a member.
        audit!.Before.ShouldContain("Underground");
        audit.After.ShouldContain("AboveGroundCovered");
    }

    [Fact]
    public async Task A_duplicate_code_is_refused_rather_than_reported_as_any_failure()
    {
        await fixture.ResetUnitAsync();
        await AddAsync("A", 1, 3);

        Guid spaceId;

        await using (var find = fixture.CreateContext())
        {
            spaceId = await find.ParkingSpaces.Where(s => s.Code == "A2").Select(s => s.Id).FirstAsync();
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditSpaceAsync(
            spaceId, new SpaceEdit("A3", null, null, null), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("already called A3");
    }

    [Fact]
    public async Task Removing_one_space_is_audited_against_the_project()
    {
        await fixture.ResetUnitAsync();
        await AddAsync("A", 1, 3);

        Guid spaceId;

        await using (var find = fixture.CreateContext())
        {
            spaceId = await find.ParkingSpaces.Where(s => s.Code == "A2").Select(s => s.Id).FirstAsync();
        }

        await using var db = fixture.CreateContext();
        (await Service(db).DeleteSpaceAsync(spaceId, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Deleted>();

        // Against the project, not the bay: recorded against the bay's own id it would be
        // unfindable the moment the bay was gone, which is exactly when it is looked for.
        var audit = await LastAuditAsync("parking.space.removed");
        audit.ShouldNotBeNull();
        audit!.EntityId.ShouldBe(fixture.ProjectId);
        audit.Before.ShouldContain("A2");
    }

    [Fact]
    public async Task A_space_somebody_is_using_is_kept_and_named()
    {
        await fixture.ResetUnitAsync();
        await AddAsync("A", 1, 3);

        await using (var setup = fixture.CreateContext())
        {
            await setup.ParkingSpaces
                .Where(s => s.Code == "A2")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, UnitState.Reserved));
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).RemoveRangeAsync(
            fixture.ProjectId, new SpaceRange("A", 1, 3), Actor, "dev", CancellationToken.None);

        var removed = (SpacesRemoved)result.ShouldBeOfType<AdminWriteResult.Payload>().Value;

        // A range is a blunt instrument, so the ones in use are kept rather than the whole
        // request being refused because of one.
        removed.Removed.ShouldBe(2);
        removed.Kept.ShouldBe(["A2"]);

        await using var verify = fixture.CreateContext();
        (await verify.ParkingSpaces.AnyAsync(s => s.Code == "A2")).ShouldBeTrue();
    }

    [Fact]
    public async Task Setting_a_price_records_what_it_was()
    {
        await fixture.ResetUnitAsync();

        await using (var first = fixture.CreateContext())
        {
            await Service(first).SetPriceAsync(
                fixture.ProjectId, ParkingSpaceKind.Underground, 12_000m, 1_000m,
                Actor, "dev", CancellationToken.None);
        }

        await using (var second = fixture.CreateContext())
        {
            await Service(second).SetPriceAsync(
                fixture.ProjectId, ParkingSpaceKind.Underground, 9_000m, 1_000m,
                Actor, "dev", CancellationToken.None);
        }

        // "Who changed underground parking from 12,000 to 9,000" is the question this exists for.
        var audit = await LastAuditAsync("parking.price.set");
        audit.ShouldNotBeNull();
        audit!.Before.ShouldContain("12000");
        audit.After.ShouldContain("9000");
    }

    [Fact]
    public async Task Setting_the_same_price_twice_changes_nothing()
    {
        await fixture.ResetUnitAsync();

        await using (var first = fixture.CreateContext())
        {
            await Service(first).SetPriceAsync(
                fixture.ProjectId, ParkingSpaceKind.Underground, 12_000m, 1_000m,
                Actor, "dev", CancellationToken.None);
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).SetPriceAsync(
            fixture.ProjectId, ParkingSpaceKind.Underground, 12_000m, 1_000m,
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.NoChange>();
    }

    [Fact]
    public async Task Dropping_the_maximum_to_zero_is_audited()
    {
        await fixture.ResetUnitAsync();

        await using (var open = fixture.CreateContext())
        {
            await Service(open).SetMaxPerReservationAsync(
                fixture.ProjectId, 2, Actor, "dev", CancellationToken.None);
        }

        await using var db = fixture.CreateContext();
        await Service(db).SetMaxPerReservationAsync(
            fixture.ProjectId, 0, Actor, "dev", CancellationToken.None);

        // Setting this to zero silently stops anybody adding parking to a sale, and the screen
        // gives no hint that somebody chose it.
        var audit = await LastAuditAsync("parking.max.set");
        audit.ShouldNotBeNull();
        audit!.Before.ShouldContain("2");
        audit.After.ShouldContain("0");
    }

    [Fact]
    public async Task An_unknown_project_is_not_found()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        (await Service(db).AddSpacesAsync(
            Guid.CreateVersion7(), new SpaceRange("A", 1, 2), ParkingSpaceKind.Underground, -1,
            Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.NotFound>();

        (await Service(db).SetMaxPerReservationAsync(
            Guid.CreateVersion7(), 2, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.NotFound>();
    }
}
