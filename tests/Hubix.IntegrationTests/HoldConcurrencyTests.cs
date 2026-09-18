using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// The release gate for M3.
/// </summary>
/// <remarks>
/// Today Hubix prevents double-booking by a member of staff being quick with a spreadsheet.
/// If these fail, this system is no better than that, whatever else passes.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class HoldConcurrencyTests(DatabaseFixture fixture)
{
    private HoldService NewService(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<HoldService>.Instance);

    private static ProspectDetails Prospect(int n) => new(
        Email: $"buyer{n}@example.ro",
        FirstName: "Buyer",
        Surname: n.ToString(),
        Phone: null,
        City: "Bucuresti",
        Country: null,
        Locale: Cultures.Romanian,
        ConsentGiven: true,
        ConsentSource: "test");

    [Fact]
    public async Task Exactly_one_of_twenty_simultaneous_holds_wins()
    {
        await fixture.ResetUnitAsync();

        const int contenders = 20;

        // Every task opens its own connection and context, as twenty separate HTTP requests
        // would. A shared context would serialise them and prove nothing.
        var barrier = new TaskCompletionSource();

        var attempts = Enumerable.Range(0, contenders).Select(async i =>
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);

            await barrier.Task; // release them all at the same instant

            return await service.TryHoldAsync(fixture.UnitId, Prospect(i), null, CancellationToken.None);
        }).ToList();

        barrier.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        outcomes.OfType<HoldOutcome.Created>().Count().ShouldBe(1);
        outcomes.OfType<HoldOutcome.NoLongerAvailable>().Count().ShouldBe(contenders - 1);

        await using var verify = fixture.CreateContext();

        // The unit is held once...
        var unit = await verify.Units.SingleAsync(u => u.Id == fixture.UnitId);
        unit.State.ShouldBe(UnitState.Held);

        // ...and there is exactly one live hold, which the filtered unique index also guarantees.
        var live = await verify.Holds.CountAsync(h => h.UnitId == fixture.UnitId && h.ReleasedAt == null);
        live.ShouldBe(1);
    }

    [Fact]
    public async Task One_person_cannot_step_over_the_hold_cap_by_asking_all_at_once()
    {
        await fixture.ResetUnitAsync();

        // Not the default of three, so the test also proves the cap is the project's own.
        const int cap = 2;

        await using (var setup = fixture.CreateContext())
        {
            await setup.Projects.Where(p => p.Id == fixture.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.MaxActiveHoldsPerProspect, cap));
        }

        // More units than the cap allows, each free, all in the one project.
        const int contenders = cap * 2;

        var units = await fixture.AddUnitsAsync(contenders);
        var buyer = Prospect(1);

        // The buyer exists before the rush. Anybody near this cap has held something here
        // already, so this is the realistic case — and it keeps the test about the cap: a
        // brand-new address racing with itself collides on IX_Prospects_Email instead, which
        // is a different fault in a different place.
        var warmUp = units[^1];

        await using (var seed = fixture.CreateContext())
        {
            (await NewService(seed).TryHoldAsync(warmUp, buyer, null, CancellationToken.None))
                .ShouldBeOfType<HoldOutcome.Created>();

            await seed.Holds.Where(h => h.UnitId == warmUp)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.ReleasedAt, DateTimeOffset.UtcNow));

            await seed.Units.Where(u => u.Id == warmUp)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Available));
        }

        // One person, one project, several apartments, all at the same instant. The count
        // that enforces the cap used to be taken before the transaction opened, so every
        // request read the same number and every one of them thought it was under the limit.
        var barrier = new TaskCompletionSource();

        var attempts = units.Select(async unitId =>
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);

            await barrier.Task;

            return await service.TryHoldAsync(unitId, buyer, null, CancellationToken.None);
        }).ToList();

        barrier.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        outcomes.OfType<HoldOutcome.Created>().Count()
            .ShouldBe(cap);

        outcomes.OfType<HoldOutcome.TooManyHolds>().Count()
            .ShouldBe(contenders - cap);

        await using var verify = fixture.CreateContext();

        // What the cap exists for: one person cannot lock more of a project's inventory than
        // the rule allows, however they ask.
        var held = await verify.Holds
            .CountAsync(h => h.ReleasedAt == null && h.Unit!.ProjectId == fixture.ProjectId);

        held.ShouldBe(cap);
    }

    [Fact]
    public async Task A_new_buyer_asking_for_two_units_at_once_gets_one_record()
    {
        await fixture.ResetUnitAsync();

        const int contenders = 4;

        var units = await fixture.AddUnitsAsync(contenders);

        // Nobody by this name yet. Every one of these requests looks for the record, finds
        // nothing, and writes one — and the unique index on the address lets exactly one of
        // them through. That used to surface as a 500 on a public form because somebody else
        // was filling it in at the same moment.
        var newcomer = Prospect(99);

        var barrier = new TaskCompletionSource();

        var attempts = units.Select(async unitId =>
        {
            await using var db = fixture.CreateContext();
            var service = NewService(db);

            await barrier.Task;

            return await service.TryHoldAsync(unitId, newcomer, null, CancellationToken.None);
        }).ToList();

        barrier.SetResult();

        // The point of the test: none of them throws.
        var outcomes = await Task.WhenAll(attempts);

        outcomes.OfType<HoldOutcome.Created>().Count().ShouldBeGreaterThan(0);

        await using var verify = fixture.CreateContext();

        // One person, one record. Two would be the same buyer split in half, with their holds
        // and consent divided between the copies.
        var records = await verify.Prospects.CountAsync(p => p.Email == newcomer.Email);
        records.ShouldBe(1);
    }

    [Fact]
    public async Task A_second_hold_on_a_held_unit_is_refused()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var service = NewService(db);

        var first = await service.TryHoldAsync(fixture.UnitId, Prospect(1), null, CancellationToken.None);
        first.ShouldBeOfType<HoldOutcome.Created>();

        var second = await service.TryHoldAsync(fixture.UnitId, Prospect(2), null, CancellationToken.None);
        second.ShouldBeOfType<HoldOutcome.NoLongerAvailable>();
    }

    [Fact]
    public async Task Losing_the_race_leaves_no_trace_behind()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var service = NewService(db);

        await service.TryHoldAsync(fixture.UnitId, Prospect(1), null, CancellationToken.None);
        await service.TryHoldAsync(fixture.UnitId, Prospect(2), null, CancellationToken.None);

        await using var verify = fixture.CreateContext();

        // The loser's transaction rolled back, so there is no orphan hold row and no
        // audit entry claiming something happened.
        var holds = await verify.Holds.CountAsync(h => h.UnitId == fixture.UnitId);
        holds.ShouldBe(1);

        var audits = await verify.AuditLogs.CountAsync(a => a.Action == "hold.created");
        audits.ShouldBe(1);
    }

    [Fact]
    public async Task Releasing_an_expired_hold_returns_the_unit_to_sale()
    {
        await fixture.ResetUnitAsync();

        // A clock two days ahead makes the hold already expired, without waiting for it.
        var past = new FakeClock(DateTimeOffset.UtcNow.AddDays(-3));

        await using (var db = fixture.CreateContext())
        {
            var service = new HoldService(db, past, NullLogger<HoldService>.Instance);
            var created = await service.TryHoldAsync(fixture.UnitId, Prospect(1), null, CancellationToken.None);
            created.ShouldBeOfType<HoldOutcome.Created>();
        }

        await using (var db = fixture.CreateContext())
        {
            var service = NewService(db); // real clock: the hold is long past due
            var expired = await service.ExpireDueHoldsAsync(CancellationToken.None);

            expired.Count.ShouldBe(1);
            expired[0].UnitCode.ShouldBe("Ap.101");
        }

        await using var verify = fixture.CreateContext();

        var unit = await verify.Units.SingleAsync(u => u.Id == fixture.UnitId);
        unit.State.ShouldBe(UnitState.Available);

        var hold = await verify.Holds.SingleAsync(h => h.UnitId == fixture.UnitId);
        hold.ReleasedAt.ShouldNotBeNull();
        hold.ReleaseReason.ShouldBe(HoldReleaseReason.Expired);

        // Hubix's serial-canceller rule: the prospect who let it lapse is blocked.
        var prospect = await verify.Prospects.SingleAsync();
        prospect.LapsedHoldCount.ShouldBe(1);
        prospect.CanHold.ShouldBeFalse();
    }

    [Fact]
    public async Task The_expiry_sweep_is_idempotent()
    {
        await fixture.ResetUnitAsync();

        var past = new FakeClock(DateTimeOffset.UtcNow.AddDays(-3));

        await using (var db = fixture.CreateContext())
        {
            var service = new HoldService(db, past, NullLogger<HoldService>.Instance);
            await service.TryHoldAsync(fixture.UnitId, Prospect(1), null, CancellationToken.None);
        }

        await using var run = fixture.CreateContext();
        var sweeper = NewService(run);

        // Hangfire can fire the same job twice if a node is slow. The second pass must not
        // double-count the lapse or re-release anything.
        var first = await sweeper.ExpireDueHoldsAsync(CancellationToken.None);
        var second = await sweeper.ExpireDueHoldsAsync(CancellationToken.None);

        first.Count.ShouldBe(1);
        second.ShouldBeEmpty();

        await using var verify = fixture.CreateContext();
        var prospect = await verify.Prospects.SingleAsync();
        prospect.LapsedHoldCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_blocked_prospect_cannot_take_another_hold()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var service = NewService(db);

        var created = await service.TryHoldAsync(fixture.UnitId, Prospect(1), null, CancellationToken.None);
        var hold = created.ShouldBeOfType<HoldOutcome.Created>();

        // Cancelling counts as a lapse, per Hubix's stated rule.
        await service.ReleaseAsync(hold.HoldId, HoldReleaseReason.Cancelled, null, CancellationToken.None);

        var again = await service.TryHoldAsync(fixture.UnitId, Prospect(1), null, CancellationToken.None);
        again.ShouldBeOfType<HoldOutcome.ProspectBlocked>();

        // The unit itself went back on sale and is free for somebody else.
        var other = await service.TryHoldAsync(fixture.UnitId, Prospect(2), null, CancellationToken.None);
        other.ShouldBeOfType<HoldOutcome.Created>();
    }

    [Fact]
    public async Task A_staff_release_does_not_count_against_the_prospect()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var service = NewService(db);

        var created = await service.TryHoldAsync(fixture.UnitId, Prospect(1), null, CancellationToken.None);
        var hold = created.ShouldBeOfType<HoldOutcome.Created>();

        await service.ReleaseAsync(hold.HoldId, HoldReleaseReason.Admin, "cristi@hubix.ro", CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var prospect = await verify.Prospects.SingleAsync();

        prospect.LapsedHoldCount.ShouldBe(0);
        prospect.CanHold.ShouldBeTrue();
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
