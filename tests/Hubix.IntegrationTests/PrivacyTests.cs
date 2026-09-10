using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Privacy;
using Hubix.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// The right to see what is held, and to have it removed.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PrivacyTests(DatabaseFixture fixture)
{
    private const string Email = "subject@example.ro";

    private static readonly Guid Actor = Guid.CreateVersion7();

    private static PrivacyService Service(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<PrivacyService>.Instance);

    private static HoldService Holds(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<HoldService>.Instance);

    private static ReservationService Reservations(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<ReservationService>.Instance);

    private static WaitlistService Waitlist(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<WaitlistService>.Instance);

    private static ProspectDetails Details() =>
        new(Email, "Test", "Subject", "+40 700 000 000", "Brasov", null, Cultures.Romanian, true, "test");

    /// <summary>Gives the subject a hold, a waiting list place and an enquiry.</summary>
    private async Task<HoldOutcome.Created> WithHistoryAsync()
    {
        await using var db = fixture.CreateContext();

        var hold = (await Holds(db).TryHoldAsync(fixture.UnitId, Details(), "::1", CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.Created>();

        await Waitlist(db).JoinAsync(fixture.ProjectId, null, Details(), CancellationToken.None);

        var prospectId = await db.Prospects.Where(p => p.Email == Email)
            .Select(p => p.Id).SingleAsync();

        db.Enquiries.Add(new Enquiry
        {
            ProjectId = fixture.ProjectId,
            ProspectId = prospectId,
            Message = "Is parking included?"
        });

        await db.SaveChangesAsync();

        return hold;
    }

    // --- Seeing it ------------------------------------------------------------------

    [Fact]
    public async Task An_export_carries_everything_held_about_a_person()
    {
        await fixture.ResetUnitAsync();
        await WithHistoryAsync();

        await using var db = fixture.CreateContext();
        var data = await Service(db).ExportAsync(Email, CancellationToken.None);

        data.ShouldNotBeNull();
        data.Email.ShouldBe(Email);
        data.FullName.ShouldBe("Test Subject");
        data.Phone.ShouldBe("+40 700 000 000");

        // What they agreed to, and where — the part that is hardest to reconstruct later,
        // which is why it has been captured since the first migration.
        data.ConsentPurposes.ShouldContain("reservations");
        data.ConsentGivenAt.ShouldNotBeNull();

        data.Holds.Count.ShouldBe(1);
        data.Holds[0].Unit.ShouldBe("Ap.101");
        data.WaitingLists.Count.ShouldBe(1);
        data.Enquiries.Count.ShouldBe(1);
        data.Enquiries[0].Message.ShouldBe("Is parking included?");
    }

    [Fact]
    public async Task Somebody_unknown_exports_as_nothing_rather_than_an_error()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        (await Service(db).ExportAsync("nobody@example.ro", CancellationToken.None)).ShouldBeNull();
    }

    // --- Removing it ----------------------------------------------------------------

    [Fact]
    public async Task Somebody_who_owes_nothing_is_deleted_outright()
    {
        await fixture.ResetUnitAsync();
        await WithHistoryAsync();

        await using var db = fixture.CreateContext();

        (await Service(db).ForgetAsync(Email, Actor, "dev@hubix.invalid", CancellationToken.None))
            .ShouldBeOfType<ForgetOutcome.Deleted>();

        await using var verify = fixture.CreateContext();
        (await verify.Prospects.AnyAsync(p => p.Email == Email)).ShouldBeFalse();
        (await verify.WaitlistEntries.AnyAsync()).ShouldBeFalse();
        (await verify.Enquiries.AnyAsync()).ShouldBeFalse();
        (await verify.Holds.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Erasing_somebody_mid_hold_puts_the_apartment_back_on_sale()
    {
        await fixture.ResetUnitAsync();
        await WithHistoryAsync();

        await using (var db = fixture.CreateContext())
        {
            (await db.Units.Where(u => u.Id == fixture.UnitId).Select(u => u.State).SingleAsync())
                .ShouldBe(UnitState.Held);

            await Service(db).ForgetAsync(Email, Actor, "dev", CancellationToken.None);
        }

        // Otherwise the unit sits held for ever with nobody left to explain why.
        await using var verify = fixture.CreateContext();
        (await verify.Units.Where(u => u.Id == fixture.UnitId).Select(u => u.State).SingleAsync())
            .ShouldBe(UnitState.Available);
    }

    [Fact]
    public async Task Somebody_with_a_reservation_is_anonymised_and_the_sale_survives()
    {
        await fixture.ResetUnitAsync();
        await WithHistoryAsync();

        Guid reservationId;

        await using (var db = fixture.CreateContext())
        {
            var created = (await Reservations(db).CreateAsync(
                    fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Created>();

            reservationId = created.ReservationId;
        }

        await using (var db = fixture.CreateContext())
        {
            var outcome = (await Service(db).ForgetAsync(Email, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ForgetOutcome.Anonymised>();

            outcome.Reason.ShouldContain("accounting");
        }

        await using var verify = fixture.CreateContext();

        // The sale is still there, with its money.
        var reservation = await verify.Reservations.AsNoTracking()
            .SingleAsync(r => r.Id == reservationId);
        reservation.SnapTotalGross.ShouldBeGreaterThan(0);

        // The person is not.
        (await verify.Prospects.AnyAsync(p => p.Email == Email)).ShouldBeFalse();

        var erased = await verify.Prospects.AsNoTracking()
            .SingleAsync(p => p.Id == reservation.ProspectId);

        erased.Email.ShouldStartWith("erased-");
        erased.Email.ShouldEndWith("@removed.invalid");
        erased.FullName.ShouldBeNull();
        erased.Phone.ShouldBeNull();
        erased.GdprConsentAt.ShouldBeNull();
        erased.GdprConsentPurpose.ShouldBeEmpty();
        erased.DeletionRequestedAt.ShouldNotBeNull();

        // Marketing data goes even when the contract stays.
        (await verify.WaitlistEntries.AnyAsync(w => w.ProspectId == erased.Id)).ShouldBeFalse();
        (await verify.Enquiries.AnyAsync(e => e.ProspectId == erased.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_reservation_with_no_money_against_it_does_not_hold_a_person()
    {
        await fixture.ResetUnitAsync();
        await WithHistoryAsync();

        await using (var db = fixture.CreateContext())
        {
            var created = (await Reservations(db).CreateAsync(
                    fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Created>();

            await Reservations(db).MoveAsync(
                created.ReservationId, ReservationState.Cancelled, "Changed their mind",
                Actor, "dev", CancellationToken.None);
        }

        // Nothing was ever paid, so there is nothing an accountant needs.
        await using var db2 = fixture.CreateContext();
        (await Service(db2).ForgetAsync(Email, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ForgetOutcome.Deleted>();
    }

    [Fact]
    public async Task The_audit_trail_keeps_its_rows_and_loses_the_name()
    {
        await fixture.ResetUnitAsync();
        await WithHistoryAsync();

        int before;
        await using (var db = fixture.CreateContext())
        {
            before = await db.AuditLogs.CountAsync();
            before.ShouldBeGreaterThan(0);

            await Service(db).ForgetAsync(Email, Actor, "dev", CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();

        // Erasing the evidence that an erasure happened would defeat the point of a trail.
        (await verify.AuditLogs.CountAsync()).ShouldBeGreaterThan(before);
        (await verify.AuditLogs.AnyAsync(a => a.ActorLabel == Email)).ShouldBeFalse();
        (await verify.AuditLogs.AnyAsync(a => a.ActorLabel == "[erased]")).ShouldBeTrue();
        (await verify.AuditLogs.AnyAsync(a => a.Action == "prospect.deleted")).ShouldBeTrue();
    }

    [Fact]
    public async Task Erasing_somebody_unknown_says_so_rather_than_pretending()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        (await Service(db).ForgetAsync("nobody@example.ro", Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ForgetOutcome.NotFound>();
    }
}
