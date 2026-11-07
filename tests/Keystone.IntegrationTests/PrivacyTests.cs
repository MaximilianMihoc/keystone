using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Privacy;
using Keystone.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// The right to see what is held, and to have it removed.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class PrivacyTests(DatabaseFixture fixture)
{
    private const string Email = "subject@example.ro";

    private static readonly Guid Actor = Guid.CreateVersion7();

    private static PrivacyService Service(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<PrivacyService>.Instance);

    private static HoldService Holds(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<HoldService>.Instance);

    private static ReservationService Reservations(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<ReservationService>.Instance);

    private static WaitlistService Waitlist(KeystoneDbContext db) =>
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

        (await Service(db).ForgetAsync(Email, Actor, "dev@keystone.invalid", CancellationToken.None))
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
        erased.AnonymisedAt.ShouldNotBeNull();

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

    /// <summary>
    /// The point of the record: the asking and the answering are different dates, and the
    /// deadline runs from the first.
    /// </summary>
    [Fact]
    public async Task A_request_keeps_the_date_it_arrived_not_the_date_it_was_logged()
    {
        await fixture.ResetUnitAsync();

        var arrived = DateTimeOffset.UtcNow.AddDays(-9);

        await using var db = fixture.CreateContext();

        var logged = await Service(db).LogRequestAsync(
            Email, PrivacyRequestKind.Access, arrived, "Asked by email", Actor, "dev",
            CancellationToken.None);

        logged.RequestedAt.ShouldBe(arrived, TimeSpan.FromSeconds(1));
        logged.DueAt.ShouldBe(arrived.AddMonths(1), TimeSpan.FromSeconds(1));
        logged.IsOpen.ShouldBeTrue();
        logged.IsOverdue(DateTimeOffset.UtcNow).ShouldBeFalse();

        // Nine days in, with a month allowed. Twenty-three days later it is late.
        logged.IsOverdue(arrived.AddMonths(1).AddDays(1)).ShouldBeTrue();
    }

    /// <summary>A date in the future is a typo, and would otherwise never fall due.</summary>
    [Fact]
    public async Task A_request_cannot_be_asked_for_tomorrow()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        var logged = await Service(db).LogRequestAsync(
            Email, PrivacyRequestKind.Access, DateTimeOffset.UtcNow.AddYears(1), null,
            Actor, "dev", CancellationToken.None);

        logged.RequestedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Answering_a_request_closes_it_once_and_only_once()
    {
        await fixture.ResetUnitAsync();

        Guid requestId;

        await using (var db = fixture.CreateContext())
        {
            requestId = (await Service(db).LogRequestAsync(
                Email, PrivacyRequestKind.Access, null, null, Actor, "dev",
                CancellationToken.None)).Id;

            (await Service(db).MarkAnsweredAsync(requestId, Actor, "dev", CancellationToken.None))
                .ShouldBeTrue();

            // Answering it again is not an error, but it must not move the date.
            (await Service(db).MarkAnsweredAsync(requestId, Actor, "dev", CancellationToken.None))
                .ShouldBeFalse();
        }

        await using var verify = fixture.CreateContext();
        var request = await verify.PrivacyRequests.SingleAsync(r => r.Id == requestId);

        request.IsOpen.ShouldBeFalse();
        request.Outcome.ShouldBe("answered");

        (await verify.AuditLogs.AnyAsync(
            a => a.EntityId == requestId && a.Action == "privacy.request.answered")).ShouldBeTrue();
    }

    /// <summary>
    /// The record has to outlive the person, or it proves nothing — and it must not carry
    /// their address afterwards, or it undoes the erasure it is evidence of.
    /// </summary>
    [Fact]
    public async Task Erasure_closes_the_request_and_takes_the_address_off_it()
    {
        await fixture.ResetUnitAsync();
        await WithHistoryAsync();

        Guid requestId;

        await using (var db = fixture.CreateContext())
        {
            requestId = (await Service(db).LogRequestAsync(
                Email, PrivacyRequestKind.Erasure, DateTimeOffset.UtcNow.AddDays(-2), null,
                Actor, "dev", CancellationToken.None)).Id;

            (await Service(db).ForgetAsync(Email, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ForgetOutcome.Deleted>();
        }

        await using var verify = fixture.CreateContext();

        // The person is gone.
        (await verify.Prospects.AnyAsync(p => p.Email == Email)).ShouldBeFalse();

        // The proof that they asked, and were answered inside the month, is not.
        var request = await verify.PrivacyRequests.SingleAsync(r => r.Id == requestId);

        request.Email.ShouldBeNull();
        request.Outcome.ShouldBe("deleted");
        request.CompletedAt.ShouldNotBeNull();
        request.CompletedAt!.Value.ShouldBeLessThanOrEqualTo(request.DueAt);
    }

    /// <summary>The anonymise path keeps a person's row, and must close the request too.</summary>
    [Fact]
    public async Task Erasure_that_has_to_keep_the_record_still_closes_the_request()
    {
        await fixture.ResetUnitAsync();
        await WithHistoryAsync();

        Guid requestId;

        await using (var db = fixture.CreateContext())
        {
            var created = (await Reservations(db).CreateAsync(
                    fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Created>();

            await db.Reservations
                .Where(r => r.Id == created.ReservationId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.FeePaidAt, DateTimeOffset.UtcNow));

            requestId = (await Service(db).LogRequestAsync(
                Email, PrivacyRequestKind.Erasure, null, null, Actor, "dev", CancellationToken.None)).Id;

            (await Service(db).ForgetAsync(Email, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ForgetOutcome.Anonymised>();
        }

        await using var verify = fixture.CreateContext();
        var request = await verify.PrivacyRequests.SingleAsync(r => r.Id == requestId);

        request.Outcome.ShouldBe("anonymised");
        request.Email.ShouldBeNull();
        request.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Open_requests_come_back_oldest_first()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var service = Service(db);

        var older = await service.LogRequestAsync(
            "a@example.ro", PrivacyRequestKind.Access, DateTimeOffset.UtcNow.AddDays(-20), null,
            Actor, "dev", CancellationToken.None);

        var newer = await service.LogRequestAsync(
            "b@example.ro", PrivacyRequestKind.Access, DateTimeOffset.UtcNow.AddDays(-1), null,
            Actor, "dev", CancellationToken.None);

        await service.MarkAnsweredAsync(newer.Id, Actor, "dev", CancellationToken.None);

        var open = await service.OpenRequestsAsync(CancellationToken.None);

        // Whatever is closest to its deadline is on top, and what is answered is not there.
        open.ShouldContain(r => r.Id == older.Id);
        open.ShouldNotContain(r => r.Id == newer.Id);
        open[0].RequestedAt.ShouldBeLessThanOrEqualTo(open[^1].RequestedAt);
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
