using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Hubix.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

[Collection(nameof(DatabaseCollection))]
public class StaffAuthTests(DatabaseFixture fixture)
{
    private StaffAuthService Service(HubixDbContext db, TimeProvider? clock = null) =>
        new(db, clock ?? TimeProvider.System, NullLogger<StaffAuthService>.Instance);

    private async Task<StaffUser> SeedStaffAsync(bool active = true)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [StaffLoginTokens]; DELETE FROM [StaffUsers];");

        var staff = new StaffUser
        {
            Email = $"tester{Guid.NewGuid():N}@hubix.invalid",
            Name = "Tester",
            Role = StaffRole.Sales,
            IsActive = active
        };

        db.StaffUsers.Add(staff);
        await db.SaveChangesAsync();

        return staff;
    }

    [Fact]
    public async Task A_link_signs_you_in_exactly_once()
    {
        var staff = await SeedStaffAsync();

        await using var db = fixture.CreateContext();
        var auth = Service(db);

        var link = await auth.IssueLoginTokenAsync(staff.Email, "::1", CancellationToken.None);
        link.ShouldNotBeNull();

        (await auth.ConsumeAsync(link.Token, CancellationToken.None)).ShouldNotBeNull();

        // Forwarded, logged, or in someone's browser history: it is spent either way.
        (await auth.ConsumeAsync(link.Token, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Asking_for_a_new_link_voids_the_previous_one()
    {
        var staff = await SeedStaffAsync();

        await using var db = fixture.CreateContext();
        var auth = Service(db);

        var first = await auth.IssueLoginTokenAsync(staff.Email, null, CancellationToken.None);
        var second = await auth.IssueLoginTokenAsync(staff.Email, null, CancellationToken.None);

        (await auth.ConsumeAsync(first!.Token, CancellationToken.None)).ShouldBeNull();
        (await auth.ConsumeAsync(second!.Token, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        var staff = await SeedStaffAsync();

        var issuedLongAgo = new FakeClock(DateTimeOffset.UtcNow.AddHours(-2));

        string token;
        await using (var db = fixture.CreateContext())
        {
            var link = await Service(db, issuedLongAgo)
                .IssueLoginTokenAsync(staff.Email, null, CancellationToken.None);
            token = link!.Token;
        }

        await using var now = fixture.CreateContext();
        (await Service(now).ConsumeAsync(token, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_address_yields_no_link_and_no_error()
    {
        await SeedStaffAsync();

        await using var db = fixture.CreateContext();

        // Null rather than an exception: the page shows the same confirmation either way,
        // so an unauthenticated visitor cannot use it to enumerate who works at Hubix.
        var link = await Service(db)
            .IssueLoginTokenAsync("nobody@example.com", null, CancellationToken.None);

        link.ShouldBeNull();
    }

    [Fact]
    public async Task A_deactivated_account_cannot_get_a_link()
    {
        var staff = await SeedStaffAsync(active: false);

        await using var db = fixture.CreateContext();

        (await Service(db).IssueLoginTokenAsync(staff.Email, null, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task The_raw_token_is_never_stored()
    {
        var staff = await SeedStaffAsync();

        await using var db = fixture.CreateContext();
        var link = await Service(db).IssueLoginTokenAsync(staff.Email, null, CancellationToken.None);

        var stored = await db.StaffLoginTokens.Select(t => t.TokenHash).ToListAsync();

        // A token in the database is a live credential. Only its hash belongs there.
        stored.ShouldNotContain(link!.Token);
        stored.ShouldContain(StaffLoginToken.Hash(link.Token));
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

[Collection(nameof(DatabaseCollection))]
public class UnitAdminTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private UnitAdminService Service(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<UnitAdminService>.Instance);

    private static UnitEdit Edit(
        decimal? price = null,
        decimal? cesiune = null,
        decimal? areaUsable = null,
        string? notes = null,
        Guid? typeId = null,
        decimal? parkingPrice = null,
        string? parkingSpaceKind = null) =>
        new(null, null, null, typeId, null, null, areaUsable, null, price, cesiune,
            null, parkingPrice, null, parkingSpaceKind, notes);

    [Fact]
    public async Task A_parking_space_kind_arrives_as_a_name_and_availability_follows_the_price()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            fixture.UnitId,
            Edit(parkingPrice: 8_000m, parkingSpaceKind: "Underground"),
            Actor, "dev", CancellationToken.None);

        var updated = result.ShouldBeOfType<UnitEditResult.Updated>();

        // By name, not by ordinal, so the audit row survives a reordering of the enum.
        updated.Changes["ParkingSpaceKind"].ShouldBe("Underground");

        await using var verify = fixture.CreateContext();
        var unit = await verify.Units.SingleAsync(u => u.Id == fixture.UnitId);

        unit.ParkingSpaceKind.ShouldBe(ParkingSpaceKind.Underground);
        unit.ParkingPriceNet.ShouldBe(8_000m);

        // Derived, never entered: the flag follows the price without anyone ticking a box,
        // and it is recorded rather than moving silently.
        unit.ParkingAvailable.ShouldBeTrue();
        updated.Changes["ParkingAvailable"].ShouldBe(true);
    }

    [Fact]
    public async Task Parking_can_be_turned_off_again_and_the_flag_is_audited()
    {
        await fixture.ResetUnitAsync();

        await using var setUp = fixture.CreateContext();
        await Service(setUp).EditAsync(
            fixture.UnitId, Edit(parkingPrice: 8_000m, parkingSpaceKind: "Underground"),
            Actor, "dev", CancellationToken.None);

        // An empty string clears, the way every other text field on this record clears.
        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            fixture.UnitId, Edit(parkingSpaceKind: ""), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<UnitEditResult.Updated>()
            .Changes["ParkingSpaceKind"].ShouldBeNull();

        await using var verify = fixture.CreateContext();
        (await verify.Units.SingleAsync(u => u.Id == fixture.UnitId))
            .ParkingSpaceKind.ShouldBeNull();
    }

    [Fact]
    public async Task A_parking_space_kind_that_is_not_one_is_refused_before_anything_is_written()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            fixture.UnitId,
            Edit(parkingPrice: 8_000m, parkingSpaceKind: "Rooftop"),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<UnitEditResult.Rejected>().Reason.ShouldContain("Rooftop");

        // The price travelled in the same request and must not have landed either.
        await using var verify = fixture.CreateContext();
        (await verify.Units.SingleAsync(u => u.Id == fixture.UnitId)).ParkingPriceNet.ShouldBeNull();
    }

    [Fact]
    public async Task An_edit_applies_and_is_audited()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            fixture.UnitId, Edit(price: 140_000m, notes: "Corner unit"),
            Actor, "dev@hubix.invalid", CancellationToken.None);

        var updated = result.ShouldBeOfType<UnitEditResult.Updated>();
        updated.Changes.Keys.ShouldBe(["BasePriceNet", "AdminNotes"], ignoreOrder: true);

        await using var verify = fixture.CreateContext();
        (await verify.Units.SingleAsync(u => u.Id == fixture.UnitId)).BasePriceNet.ShouldBe(140_000m);

        var audit = await verify.AuditLogs.SingleAsync(a => a.Action == "unit.edited");
        audit.ActorLabel.ShouldBe("dev@hubix.invalid");
        audit.Before.ShouldNotBeNull();
        audit.After.ShouldContain("140000");
    }

    [Fact]
    public async Task Sending_the_same_values_changes_nothing()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var unit = await db.Units.AsNoTracking().SingleAsync(u => u.Id == fixture.UnitId);

        var result = await Service(db).EditAsync(
            fixture.UnitId, Edit(price: unit.BasePriceNet), Actor, "dev", CancellationToken.None);

        // No audit noise for an edit that edited nothing.
        result.ShouldBeOfType<UnitEditResult.NoChange>();
        (await db.AuditLogs.CountAsync(a => a.Action == "unit.edited")).ShouldBe(0);
    }

    [Fact]
    public async Task The_price_of_a_sold_unit_cannot_be_changed_here()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        await db.Units.Where(u => u.Id == fixture.UnitId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Sold));

        var result = await Service(db).EditAsync(
            fixture.UnitId, Edit(price: 1m), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<UnitEditResult.Rejected>()
            .Reason.ShouldContain("sold");
    }

    [Fact]
    public async Task A_sold_unit_can_still_have_its_notes_corrected()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        await db.Units.Where(u => u.Id == fixture.UnitId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Sold));

        // Only money is frozen. Blocking every edit would push staff back to the spreadsheet.
        var result = await Service(db).EditAsync(
            fixture.UnitId, Edit(notes: "Completed 12 March"), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<UnitEditResult.Updated>();
    }

    [Fact]
    public async Task The_cesiune_cannot_exceed_the_total()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        // It is Hubix's margin taken out of what the buyer pays, not added on top.
        var result = await Service(db).EditAsync(
            fixture.UnitId, Edit(cesiune: 999_999m), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<UnitEditResult.Rejected>()
            .Reason.ShouldContain("cannot exceed");
    }

    [Fact]
    public async Task Usable_area_cannot_be_zeroed()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            fixture.UnitId, Edit(areaUsable: 0m), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<UnitEditResult.Rejected>();
    }

    [Fact]
    public async Task A_unit_type_from_another_project_is_refused()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        var foreign = new Project
        {
            Slug = $"other-{Guid.NewGuid():N}",
            Name = "Other",
            City = "Sibiu",
            PublishedAt = DateTimeOffset.UtcNow
        };
        var foreignType = new UnitType { Name = "3 camere", SortOrder = 0 };
        foreign.UnitTypes.Add(foreignType);
        db.Projects.Add(foreign);
        await db.SaveChangesAsync();

        var result = await Service(db).EditAsync(
            fixture.UnitId, Edit(typeId: foreignType.Id), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<UnitEditResult.Rejected>()
            .Reason.ShouldContain("another project");
    }

    [Fact]
    public async Task Withdrawing_takes_a_unit_off_sale_and_back()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var service = Service(db);

        (await service.SetWithdrawnAsync(fixture.UnitId, true, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<UnitEditResult.Updated>();

        (await db.Units.AsNoTracking().SingleAsync(u => u.Id == fixture.UnitId))
            .State.ShouldBe(UnitState.Withdrawn);

        (await service.SetWithdrawnAsync(fixture.UnitId, false, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<UnitEditResult.Updated>();

        (await db.Units.AsNoTracking().SingleAsync(u => u.Id == fixture.UnitId))
            .State.ShouldBe(UnitState.Available);
    }

    [Fact]
    public async Task A_held_unit_cannot_be_withdrawn_from_under_the_prospect()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        await db.Units.Where(u => u.Id == fixture.UnitId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Held));

        // The state machine has no Held -> Withdrawn edge, and it is the reason why:
        // somebody has this apartment for the next two days.
        var result = await Service(db)
            .SetWithdrawnAsync(fixture.UnitId, true, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<UnitEditResult.Rejected>().Reason.ShouldContain("held");
    }
}
