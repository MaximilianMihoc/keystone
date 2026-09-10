using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Hubix.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// Lifting and applying the block that stops a prospect holding a unit.
/// </summary>
/// <remarks>
/// The block half is already covered by <see cref="HoldConcurrencyTests"/>. What matters here is
/// the other half, which did not exist before: that an admin can undo it, and that the prospect
/// can then actually reserve again rather than merely looking unblocked in a column.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class ProspectAdminTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static ProspectAdminService Service(HubixDbContext db) =>
        new(db, NullLogger<ProspectAdminService>.Instance);

    private static HoldService Holds(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<HoldService>.Instance);

    private static ProspectDetails Buyer => new(
        Email: "buyer@example.ro",
        FullName: "Buyer",
        Phone: null,
        Locale: Cultures.Romanian,
        ConsentGiven: true,
        ConsentSource: "test");

    /// <summary>Takes a hold and cancels it, which is what blocks somebody in practice.</summary>
    private async Task<Guid> BlockByLapseAsync()
    {
        await using var db = fixture.CreateContext();
        var holds = Holds(db);

        var created = await holds.TryHoldAsync(fixture.UnitId, Buyer, null, CancellationToken.None);
        var hold = created.ShouldBeOfType<HoldOutcome.Created>();

        await holds.ReleaseAsync(hold.HoldId, HoldReleaseReason.Cancelled, null, CancellationToken.None);

        return await db.Prospects.Where(p => p.Email == Buyer.Email).Select(p => p.Id).SingleAsync();
    }

    [Fact]
    public async Task A_lifted_block_lets_them_hold_again()
    {
        await fixture.ResetUnitAsync();

        var prospectId = await BlockByLapseAsync();

        await using (var db = fixture.CreateContext())
        {
            (await Holds(db).TryHoldAsync(fixture.UnitId, Buyer, null, CancellationToken.None))
                .ShouldBeOfType<HoldOutcome.ProspectBlocked>();
        }

        await using (var db = fixture.CreateContext())
        {
            var result = await Service(db).SetBlockedAsync(
                prospectId, false, null, Actor, "admin@hubix.invalid", CancellationToken.None);

            result.ShouldBeOfType<AdminWriteResult.Updated>();
        }

        // The point of the whole feature: not that the column flipped, but that the door opened.
        await using (var db = fixture.CreateContext())
        {
            (await Holds(db).TryHoldAsync(fixture.UnitId, Buyer, null, CancellationToken.None))
                .ShouldBeOfType<HoldOutcome.Created>();
        }

        await using var verify = fixture.CreateContext();

        var prospect = await verify.Prospects.SingleAsync(p => p.Id == prospectId);
        prospect.CanHold.ShouldBeTrue();
        prospect.BlockReason.ShouldBeNull();

        // History survives the unblock: they did lapse once, and the next person to look
        // should be able to see that.
        prospect.LapsedHoldCount.ShouldBe(1);

        (await verify.AuditLogs.AnyAsync(a =>
            a.Action == "prospect.block.cleared" && a.EntityId == prospectId)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_hand_block_stops_the_next_hold()
    {
        await fixture.ResetUnitAsync();

        Guid prospectId;

        await using (var db = fixture.CreateContext())
        {
            var created = await Holds(db)
                .TryHoldAsync(fixture.UnitId, Buyer, null, CancellationToken.None);
            var hold = created.ShouldBeOfType<HoldOutcome.Created>();

            // Released by staff, so nothing counts against them yet.
            await Holds(db).ReleaseAsync(
                hold.HoldId, HoldReleaseReason.Admin, "admin@hubix.invalid", CancellationToken.None);

            prospectId = await db.Prospects
                .Where(p => p.Email == Buyer.Email).Select(p => p.Id).SingleAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var result = await Service(db).SetBlockedAsync(
                prospectId, true, "  Never turns up  ", Actor, "admin@hubix.invalid",
                CancellationToken.None);

            result.ShouldBeOfType<AdminWriteResult.Updated>();
        }

        await using (var db = fixture.CreateContext())
        {
            var refused = await Holds(db)
                .TryHoldAsync(fixture.UnitId, Buyer, null, CancellationToken.None);

            refused.ShouldBeOfType<HoldOutcome.ProspectBlocked>()
                .Reason.ShouldBe("Never turns up");
        }

        await using var verify = fixture.CreateContext();
        var prospect = await verify.Prospects.SingleAsync(p => p.Id == prospectId);

        // A staff decision is not a lapse, so the automatic count stays where it was.
        prospect.LapsedHoldCount.ShouldBe(0);

        (await verify.AuditLogs.AnyAsync(a =>
            a.Action == "prospect.block.applied" && a.EntityId == prospectId)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_block_needs_a_reason()
    {
        await fixture.ResetUnitAsync();

        var prospectId = await BlockByLapseAsync();

        await using var db = fixture.CreateContext();

        var result = await Service(db).SetBlockedAsync(
            prospectId, true, "   ", Actor, "admin@hubix.invalid", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>()
            .Reason.ShouldContain("why");
    }

    [Fact]
    public async Task Unblocking_an_unblocked_prospect_changes_nothing()
    {
        await fixture.ResetUnitAsync();

        await using (var db = fixture.CreateContext())
        {
            await Holds(db).TryHoldAsync(fixture.UnitId, Buyer, null, CancellationToken.None);
        }

        await using var db2 = fixture.CreateContext();

        var prospectId = await db2.Prospects
            .Where(p => p.Email == Buyer.Email).Select(p => p.Id).SingleAsync();

        var result = await Service(db2).SetBlockedAsync(
            prospectId, false, null, Actor, "admin@hubix.invalid", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.NoChange>();

        await using var verify = fixture.CreateContext();
        (await verify.AuditLogs.AnyAsync(a => a.EntityId == prospectId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_prospect_who_is_not_there_is_not_found()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        var result = await Service(db).SetBlockedAsync(
            Guid.CreateVersion7(), false, null, Actor, "admin@hubix.invalid", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.NotFound>();
    }
}
