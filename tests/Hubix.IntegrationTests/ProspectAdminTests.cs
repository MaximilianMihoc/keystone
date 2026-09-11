using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Hubix.Infrastructure.Privacy;
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
        FirstName: "Buyer",
        Surname: null,
        Phone: null,
        City: null,
        Country: null,
        Locale: Cultures.Romanian,
        ConsentGiven: true,
        ConsentSource: "test");

    /// <summary>Takes a hold, which is how a prospect comes to exist at all.</summary>
    private async Task<Guid> HoldingProspectAsync()
    {
        await using var db = fixture.CreateContext();

        await Holds(db).TryHoldAsync(fixture.UnitId, Buyer, null, CancellationToken.None);

        return await db.Prospects.Where(p => p.Email == Buyer.Email).Select(p => p.Id).SingleAsync();
    }

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

    /// <summary>
    /// The detail every reservation now collects, and the one field nobody writes directly.
    /// </summary>
    /// <remarks>
    /// <c>FullName</c> is a persisted computed column, so this is also the test that catches a
    /// migration that quietly turned it back into an ordinary one: an edit to the two parts
    /// would then leave the joined name stale rather than following it.
    /// </remarks>
    [Fact]
    public async Task Correcting_the_name_moves_the_joined_name_with_it()
    {
        await fixture.ResetUnitAsync();

        var prospectId = await HoldingProspectAsync();

        await using (var db = fixture.CreateContext())
        {
            var result = await Service(db).UpdateDetailsAsync(
                prospectId,
                new ProspectEdit(
                    "ion.popescu@example.ro", "Ion", "Popescu", "+40 700 111 222", "Cluj", "Spain"),
                Actor,
                "admin@hubix.invalid",
                CancellationToken.None);

            result.ShouldBeOfType<AdminWriteResult.Updated>()
                .Changes.Keys.ShouldContain("Surname");
        }

        await using var verify = fixture.CreateContext();
        var person = await verify.Prospects.SingleAsync(p => p.Id == prospectId);

        person.FullName.ShouldBe("Ion Popescu");
        person.Email.ShouldBe("ion.popescu@example.ro");
        person.City.ShouldBe("Cluj");
        person.Country.ShouldBe("Spain");

        (await verify.AuditLogs.AnyAsync(
            a => a.EntityId == prospectId && a.Action == "prospect.details.edited")).ShouldBeTrue();
    }

    /// <summary>A blank country means Romania, so it is stored as nothing rather than as "".</summary>
    [Fact]
    public async Task Clearing_the_country_stores_a_null_rather_than_a_blank()
    {
        await fixture.ResetUnitAsync();

        var prospectId = await HoldingProspectAsync();

        await using (var db = fixture.CreateContext())
        {
            await Service(db).UpdateDetailsAsync(
                prospectId,
                new ProspectEdit(Buyer.Email, "Buyer", null, null, "Brașov", "   "),
                Actor,
                "admin@hubix.invalid",
                CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var person = await verify.Prospects.SingleAsync(p => p.Id == prospectId);

        person.Country.ShouldBeNull();
        person.City.ShouldBe("Brașov");

        // One part only, and the joined name is that part rather than "Buyer ".
        person.FullName.ShouldBe("Buyer");
    }

    /// <summary>
    /// Correcting a typo'd address must take whatever they asked for with it.
    /// </summary>
    /// <remarks>
    /// A privacy request has no foreign key — it has to outlive an erasure that deletes the
    /// prospect — so the address is the only thread between them. Left behind, an open request
    /// would vanish from their drawer and sit on the deadline queue answerable by nobody.
    /// </remarks>
    [Fact]
    public async Task Correcting_an_address_takes_their_open_privacy_requests_with_it()
    {
        await fixture.ResetUnitAsync();

        var prospectId = await HoldingProspectAsync();

        Guid openId;
        Guid answeredId;

        await using (var db = fixture.CreateContext())
        {
            var privacy = new PrivacyService(db, TimeProvider.System,
                NullLogger<PrivacyService>.Instance);

            openId = (await privacy.LogRequestAsync(
                Buyer.Email, PrivacyRequestKind.Erasure, null, null, Actor, "dev",
                CancellationToken.None)).Id;

            answeredId = (await privacy.LogRequestAsync(
                Buyer.Email, PrivacyRequestKind.Access, null, null, Actor, "dev",
                CancellationToken.None)).Id;

            await privacy.MarkAnsweredAsync(answeredId, Actor, "dev", CancellationToken.None);

            await Service(db).UpdateDetailsAsync(
                prospectId,
                new ProspectEdit("corrected@example.ro", "Buyer", null, null, null, null),
                Actor,
                "admin@hubix.invalid",
                CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();

        (await verify.PrivacyRequests.SingleAsync(r => r.Id == openId))
            .Email.ShouldBe("corrected@example.ro");

        // A closed one records what was true when it was closed, and does not move.
        (await verify.PrivacyRequests.SingleAsync(r => r.Id == answeredId))
            .Email.ShouldBe(Buyer.Email);
    }

    [Fact]
    public async Task An_email_somebody_else_holds_is_refused()
    {
        await fixture.ResetUnitAsync();

        var prospectId = await HoldingProspectAsync();

        await using var db = fixture.CreateContext();

        db.Prospects.Add(new Prospect { Email = "taken@example.ro", FirstName = "Someone" });
        await db.SaveChangesAsync();

        var result = await Service(db).UpdateDetailsAsync(
            prospectId,
            new ProspectEdit("taken@example.ro", "Buyer", null, null, null, null),
            Actor,
            "admin@hubix.invalid",
            CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>()
            .Reason.ShouldContain("taken@example.ro");
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
