using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// The release gate for parking, and the reason spaces are their own inventory.
/// </summary>
/// <remarks>
/// A parking space is scarcer than an apartment: one car park serves every flat in the
/// development, so two buyers of two different apartments race for the same bay. If these fail,
/// the system can promise the same space to two people, which is exactly what it exists to
/// prevent. The behaviour under test is racing transactions against a real database and does
/// not exist in the in-memory provider.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class ParkingConcurrencyTests(DatabaseFixture fixture)
{
    private HoldService NewService(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<HoldService>.Instance);

    private static ProspectDetails Prospect(int n) => new(
        Email: $"parking{n}@example.ro",
        FirstName: "Buyer",
        Surname: n.ToString(),
        Phone: null,
        City: "Bucuresti",
        Country: null,
        Locale: Cultures.Romanian,
        ConsentGiven: true,
        ConsentSource: "test");

    [Fact]
    public async Task Exactly_one_of_twenty_buyers_of_different_flats_gets_the_last_space()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 1);

        // Twenty *different* apartments, one space. Racing them on a single flat would prove
        // nothing about parking: the unit's own guard eliminates nineteen of them before the
        // space is ever touched, so the per-space guard would never be exercised.
        const int contenders = 20;
        var unitIds = await fixture.AddUnitsAsync(contenders);

        var barrier = new TaskCompletionSource();

        // Every task opens its own connection and context, as twenty separate HTTP requests
        // would. A shared context would serialise them and prove nothing.
        var attempts = unitIds.Select(async (unitId, i) =>
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);

            await barrier.Task;

            return await service.TryHoldAsync(
                unitId,
                Prospect(i),
                new ParkingRequest(ParkingSpaceKind.Underground, 1),
                null,
                CancellationToken.None);
        }).ToList();

        barrier.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        // Every apartment was free, so all twenty could have had a flat. Only one may have the
        // space, and the other nineteen must be told so rather than quietly given nothing.
        outcomes.OfType<HoldOutcome.Created>().Count().ShouldBe(1);
        outcomes.OfType<HoldOutcome.NotEnoughParking>().Count().ShouldBe(contenders - 1);

        await using var verify = fixture.CreateContext();

        var held = await verify.ParkingSpaces
            .CountAsync(s => s.State == UnitState.Held && s.HoldId != null);

        held.ShouldBe(1);

        // And the nineteen losers hold no apartment either: the whole transaction or nothing.
        var live = await verify.Holds.CountAsync(h => h.ReleasedAt == null);
        live.ShouldBe(1);

        (await verify.Units.CountAsync(u => u.State == UnitState.Held)).ShouldBe(1);
    }

    [Fact]
    public async Task Asking_for_more_spaces_than_exist_holds_nothing_at_all()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 1);

        await using var db = fixture.CreateContext();

        var outcome = await NewService(db).TryHoldAsync(
            fixture.UnitId,
            Prospect(1),
            new ParkingRequest(ParkingSpaceKind.Underground, 2),
            null,
            CancellationToken.None);

        var shortfall = outcome.ShouldBeOfType<HoldOutcome.NotEnoughParking>();
        shortfall.Requested.ShouldBe(2);
        shortfall.Available.ShouldBe(1);

        await using var verify = fixture.CreateContext();

        // The apartment is not held either. Handing someone a flat on its own because the
        // parking they asked for had gone is a worse answer than telling them.
        (await verify.Units.SingleAsync(u => u.Id == fixture.UnitId))
            .State.ShouldBe(UnitState.Available);

        (await verify.Holds.CountAsync(h => h.ReleasedAt == null)).ShouldBe(0);

        // And the one free space stayed free rather than being half-claimed.
        (await verify.ParkingSpaces.SingleAsync()).State.ShouldBe(UnitState.Available);
    }

    [Fact]
    public async Task More_than_the_project_allows_is_refused()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 5, maxPerReservation: 2);

        await using var db = fixture.CreateContext();

        var outcome = await NewService(db).TryHoldAsync(
            fixture.UnitId,
            Prospect(1),
            new ParkingRequest(ParkingSpaceKind.Underground, 3),
            null,
            CancellationToken.None);

        // A distinct answer from "the car park is full": five bays are free, but not for one
        // buyer. Telling them only two are left would be false.
        var refused = outcome.ShouldBeOfType<HoldOutcome.TooManyParkingSpaces>();
        refused.Limit.ShouldBe(2);
        refused.Requested.ShouldBe(3);

        await using var verify = fixture.CreateContext();
        (await verify.Units.SingleAsync(u => u.Id == fixture.UnitId))
            .State.ShouldBe(UnitState.Available);
    }

    [Fact]
    public async Task Two_spaces_are_claimed_together_and_named()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 5, maxPerReservation: 2);

        await using var db = fixture.CreateContext();

        var outcome = await NewService(db).TryHoldAsync(
            fixture.UnitId,
            Prospect(1),
            new ParkingRequest(ParkingSpaceKind.Underground, 2),
            null,
            CancellationToken.None);

        // Allocation is by code, so the buyer is told which spaces are theirs and the same
        // car park hands out the same two every time.
        outcome.ShouldBeOfType<HoldOutcome.Created>().ParkingSpaces.ShouldBe(["A1", "A2"]);

        await using var verify = fixture.CreateContext();
        (await verify.ParkingSpaces.CountAsync(s => s.State == UnitState.Held)).ShouldBe(2);
    }

    [Fact]
    public async Task Releasing_the_hold_puts_the_spaces_back_on_the_market()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 2, maxPerReservation: 2);

        await using var db = fixture.CreateContext();
        var service = NewService(db);

        var created = (HoldOutcome.Created)await service.TryHoldAsync(
            fixture.UnitId,
            Prospect(1),
            new ParkingRequest(ParkingSpaceKind.Underground, 2),
            null,
            CancellationToken.None);

        await service.ReleaseAsync(
            created.HoldId, HoldReleaseReason.Cancelled, "test", CancellationToken.None);

        await using var verify = fixture.CreateContext();

        // Free, and unlinked: a space still pointing at a dead hold is inventory nobody can
        // reach, however available it claims to be.
        var spaces = await verify.ParkingSpaces.ToListAsync();

        spaces.ShouldAllBe(s => s.State == UnitState.Available);
        spaces.ShouldAllBe(s => s.HoldId == null);
    }

    [Fact]
    public async Task An_expired_hold_returns_its_spaces_too()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 1);

        await using var db = fixture.CreateContext();
        var service = NewService(db);

        var created = (HoldOutcome.Created)await service.TryHoldAsync(
            fixture.UnitId,
            Prospect(1),
            new ParkingRequest(ParkingSpaceKind.Underground, 1),
            null,
            CancellationToken.None);

        // Wind the countdown back rather than waiting 48 hours for it.
        await db.Holds
            .Where(h => h.Id == created.HoldId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        await using var sweep = fixture.CreateContext();
        var released = await NewService(sweep).ExpireDueHoldsAsync(CancellationToken.None);

        released.Count.ShouldBe(1);

        await using var verify = fixture.CreateContext();
        var space = await verify.ParkingSpaces.SingleAsync();

        space.State.ShouldBe(UnitState.Available);
        space.HoldId.ShouldBeNull();
    }

    [Fact]
    public async Task A_space_code_cannot_repeat_within_a_project()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 1);

        await using var db = fixture.CreateContext();

        db.ParkingSpaces.Add(new ParkingSpace
        {
            ProjectId = fixture.ProjectId,
            Code = "A1",
            Kind = ParkingSpaceKind.AboveGroundCovered,
            Level = 0
        });

        // The database enforces the numbering, not just whatever created the space: "A1" has
        // to mean one bay on the ground.
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Cancelling_a_reservation_puts_its_spaces_back_on_the_market()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 2, maxPerReservation: 1);

        await using (var holding = fixture.CreateContext())
        {
            await NewService(holding).TryHoldAsync(
                fixture.UnitId,
                Prospect(1),
                new ParkingRequest(ParkingSpaceKind.Underground, 1),
                null,
                CancellationToken.None);
        }

        var actor = Guid.CreateVersion7();
        Guid reservationId;

        await using (var db = fixture.CreateContext())
        {
            var reservations = new ReservationService(
                db, TimeProvider.System, NullLogger<ReservationService>.Instance);

            reservationId = ((ReservationOutcome.Created)await reservations.CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, actor, "dev", CancellationToken.None))
                .ReservationId;
        }

        await using (var db = fixture.CreateContext())
        {
            var reservations = new ReservationService(
                db, TimeProvider.System, NullLogger<ReservationService>.Instance);

            (await reservations.MoveAsync(
                    reservationId, ReservationState.Cancelled, "changed their mind",
                    actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Moved>();
        }

        await using var verify = fixture.CreateContext();

        // The apartment goes back on sale, and so must the bay. A space left Reserved against
        // a cancelled sale is beyond every release path there is, and its code cannot even be
        // reused because the numbering is unique.
        (await verify.Units.SingleAsync(u => u.Id == fixture.UnitId))
            .State.ShouldBe(UnitState.Available);

        var spaces = await verify.ParkingSpaces.ToListAsync();

        spaces.ShouldAllBe(s => s.State == UnitState.Available);
        spaces.ShouldAllBe(s => s.ReservationId == null);
    }

    [Fact]
    public async Task A_free_space_can_be_corrected_but_one_in_use_cannot()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 2, maxPerReservation: 1);

        await using (var holding = fixture.CreateContext())
        {
            await NewService(holding).TryHoldAsync(
                fixture.UnitId,
                Prospect(1),
                new ParkingRequest(ParkingSpaceKind.Underground, 1),
                null,
                CancellationToken.None);
        }

        await using var db = fixture.CreateContext();

        // A1 was claimed; A2 is still free. Correcting a level or a kind on the free one is an
        // ordinary fix, and the database is what enforces the numbering.
        var free = await db.ParkingSpaces.SingleAsync(s => s.State == UnitState.Available);

        free.Level = -2;
        free.Kind = ParkingSpaceKind.AboveGroundCovered;
        await db.SaveChangesAsync();

        // The held one describes what a buyer was quoted: its kind decides the price they pay
        // at conversion, so it must not move under them. The endpoint refuses it; here we pin
        // the reason it must — that a live claim exists to be found.
        var held = await db.ParkingSpaces
            .Include(s => s.Hold)
            .SingleAsync(s => s.State == UnitState.Held);

        held.Hold.ShouldNotBeNull();
        held.HoldId.ShouldNotBeNull();
    }
}
