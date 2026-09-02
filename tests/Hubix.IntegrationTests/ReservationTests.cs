using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// The pipeline against a real database: the unit follows the reservation, the agreed price
/// does not move, and two people cannot sell the same apartment.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ReservationTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static ReservationService Service(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<ReservationService>.Instance);

    private static HoldService Holds(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<HoldService>.Instance);

    private static ProspectDetails Buyer(string email = "buyer@example.ro") =>
        new(email, "Ion Popescu", null, Cultures.Romanian, true, "test");

    /// <summary>Puts the fixture's unit on hold and returns the resulting hold.</summary>
    private async Task<HoldOutcome.Created> HoldItAsync()
    {
        await using var db = fixture.CreateContext();

        return (await Holds(db).TryHoldAsync(fixture.UnitId, Buyer(), null, CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.Created>();
    }

    /// <summary>Starts a reservation from a hold and returns its id.</summary>
    private async Task<Guid> ReserveAsync()
    {
        await HoldItAsync();

        await using var db = fixture.CreateContext();

        var created = (await Service(db).CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Created>();

        return created.ReservationId;
    }

    private async Task<ReservationOutcome> MoveAsync(Guid id, ReservationState target, string? reason = null)
    {
        await using var db = fixture.CreateContext();
        return await Service(db).MoveAsync(id, target, reason, Actor, "dev", CancellationToken.None);
    }

    private async Task<Reservation> LoadAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.Reservations.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<UnitState> UnitStateAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.Units.Where(u => u.Id == fixture.UnitId).Select(u => u.State).SingleAsync();
    }

    // --- Starting one ---------------------------------------------------------------

    [Fact]
    public async Task Converting_a_hold_reserves_the_unit_and_spends_the_hold()
    {
        await fixture.ResetUnitAsync();
        var hold = await HoldItAsync();

        await using var db = fixture.CreateContext();
        var created = (await Service(db).CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Created>();

        created.State.ShouldBe(ReservationState.PendingFee);
        (await UnitStateAsync()).ShouldBe(UnitState.Reserved);

        await using var verify = fixture.CreateContext();
        var spent = await verify.Holds.AsNoTracking().SingleAsync(h => h.Id == hold.HoldId);

        // Converted, not expired or cancelled: the unit moved on rather than came back, and
        // the prospect must not be counted as having let a hold lapse.
        spent.ReleasedAt.ShouldNotBeNull();
        spent.ReleaseReason.ShouldBe(HoldReleaseReason.Converted);

        var prospect = await verify.Prospects.AsNoTracking()
            .SingleAsync(p => p.Email == "buyer@example.ro");

        prospect.CanHold.ShouldBeTrue();
        prospect.LapsedHoldCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_agreed_price_is_frozen_onto_the_reservation()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        var seeded = DatabaseFixture.SeedPrice;
        var reservation = await LoadAsync(id);

        reservation.SnapBasePriceNet.ShouldBe(seeded.Price);
        reservation.SnapCesiuneNet.ShouldBe(seeded.Cesiune);
        reservation.SnapTranches.Count.ShouldBe(2);
        reservation.SnapTranches.Sum(t => t.Net).ShouldBe(seeded.Price);
        reservation.SnapReservationFee.ShouldBe(4_000m);
    }

    [Fact]
    public async Task Raising_the_unit_price_afterwards_does_not_move_an_agreed_reservation()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        var agreed = (await LoadAsync(id)).SnapTotalGross;

        await using (var db = fixture.CreateContext())
        {
            // Hubix raises prices mid-launch; the Lake Home document describes exactly this.
            await db.Units.Where(u => u.Id == fixture.UnitId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.BasePriceNet, 200_000m));
        }

        (await LoadAsync(id)).SnapTotalGross.ShouldBe(agreed);
    }

    [Fact]
    public async Task A_reservation_can_be_recorded_on_a_unit_the_spreadsheet_already_calls_reserved()
    {
        await fixture.ResetUnitAsync();

        await using (var db = fixture.CreateContext())
        {
            await db.Units.Where(u => u.Id == fixture.UnitId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Reserved));
        }

        // Every project migrated from a spreadsheet arrives with sales already in flight.
        await using var context = fixture.CreateContext();
        var result = await Service(context).CreateAsync(
            fixture.UnitId, Buyer("existing@example.ro"), BuyerType.Individual,
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<ReservationOutcome.Created>();
        (await UnitStateAsync()).ShouldBe(UnitState.Reserved);
    }

    [Fact]
    public async Task Recording_one_without_a_hold_and_without_a_buyer_is_refused()
    {
        await fixture.ResetUnitAsync();

        await using (var db = fixture.CreateContext())
        {
            await db.Units.Where(u => u.Id == fixture.UnitId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Reserved));
        }

        await using var context = fixture.CreateContext();
        var result = await Service(context).CreateAsync(
            fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<ReservationOutcome.Refused>().Reason.ShouldContain("email address");
    }

    [Fact]
    public async Task An_available_unit_cannot_be_reserved_directly()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateAsync(
            fixture.UnitId, Buyer(), BuyerType.Individual, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<ReservationOutcome.Refused>().Reason.ShouldContain("available");
    }

    [Fact]
    public async Task A_unit_cannot_carry_two_reservations()
    {
        await fixture.ResetUnitAsync();
        await ReserveAsync();

        await using var db = fixture.CreateContext();
        var second = await Service(db).CreateAsync(
            fixture.UnitId, Buyer("second@example.ro"), BuyerType.Individual,
            Actor, "dev", CancellationToken.None);

        second.ShouldBeOfType<ReservationOutcome.Refused>().Reason.ShouldContain("already has");
    }

    [Fact]
    public async Task Exactly_one_of_ten_simultaneous_conversions_wins()
    {
        await fixture.ResetUnitAsync();
        await HoldItAsync();

        const int contenders = 10;
        var barrier = new TaskCompletionSource();

        // Ten members of staff clicking the same button, each on their own connection.
        var attempts = Enumerable.Range(0, contenders).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            var service = Service(db);

            await barrier.Task;

            return await service.CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None);
        }).ToList();

        barrier.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        outcomes.OfType<ReservationOutcome.Created>().Count().ShouldBe(1);

        await using var verify = fixture.CreateContext();
        (await verify.Reservations.CountAsync(r => r.UnitId == fixture.UnitId)).ShouldBe(1);
    }

    // --- Moving through it ----------------------------------------------------------

    [Fact]
    public async Task A_step_forward_stamps_the_date_that_stage_is_about()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        (await MoveAsync(id, ReservationState.FeePaid)).ShouldBeOfType<ReservationOutcome.Moved>();

        var reservation = await LoadAsync(id);
        reservation.State.ShouldBe(ReservationState.FeePaid);
        reservation.FeePaidAt.ShouldNotBeNull();

        // Still Reserved: the fee changes the pipeline, not the unit.
        (await UnitStateAsync()).ShouldBe(UnitState.Reserved);
    }

    [Fact]
    public async Task Signing_the_cesiune_contracts_the_unit()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        (await MoveAsync(id, ReservationState.CesiuneSigned))
            .ShouldBeOfType<ReservationOutcome.Moved>()
            .UnitState.ShouldBe(UnitState.Contracted);

        (await LoadAsync(id)).CesiuneSignedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_jump_to_the_end_walks_the_unit_through_contracted()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        // Reserved straight to Sold is not a transition the state machine allows. Skipping
        // stages must not turn it into one.
        (await MoveAsync(id, ReservationState.Completed))
            .ShouldBeOfType<ReservationOutcome.Moved>()
            .UnitState.ShouldBe(UnitState.Sold);

        var reservation = await LoadAsync(id);
        reservation.CompletedAt.ShouldNotBeNull();

        // The stages jumped over keep their empty dates. Nobody knows when they happened.
        reservation.FeePaidAt.ShouldBeNull();
        reservation.CesiuneSignedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Booking_the_notary_needs_the_appointment_date_first()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        (await MoveAsync(id, ReservationState.NotaryScheduled))
            .ShouldBeOfType<ReservationOutcome.Refused>()
            .Reason.ShouldContain("appointment");

        await using (var db = fixture.CreateContext())
        {
            await Service(db).EditAsync(
                id,
                new ReservationEdit(null, null, DateTimeOffset.UtcNow.AddDays(7), null, null),
                Actor, "dev", CancellationToken.None);
        }

        (await MoveAsync(id, ReservationState.NotaryScheduled))
            .ShouldBeOfType<ReservationOutcome.Moved>();
    }

    [Fact]
    public async Task A_step_back_clears_the_date_and_returns_the_unit()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        await MoveAsync(id, ReservationState.CesiuneSigned);
        (await UnitStateAsync()).ShouldBe(UnitState.Contracted);

        // Somebody marked the cesiune signed and it was not.
        (await MoveAsync(id, ReservationState.NotaryScheduled))
            .ShouldBeOfType<ReservationOutcome.Moved>();

        (await LoadAsync(id)).CesiuneSignedAt.ShouldBeNull();
        (await UnitStateAsync()).ShouldBe(UnitState.Reserved);
    }

    [Fact]
    public async Task A_completed_sale_cannot_be_reopened()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        await MoveAsync(id, ReservationState.Completed);

        (await MoveAsync(id, ReservationState.CesiuneSigned))
            .ShouldBeOfType<ReservationOutcome.Refused>();

        (await UnitStateAsync()).ShouldBe(UnitState.Sold);
    }

    [Fact]
    public async Task Cancelling_puts_the_apartment_back_on_sale()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        await MoveAsync(id, ReservationState.FeePaid);

        (await MoveAsync(id, ReservationState.Cancelled, "Buyer withdrew"))
            .ShouldBeOfType<ReservationOutcome.Moved>();

        var reservation = await LoadAsync(id);
        reservation.CancelledAt.ShouldNotBeNull();
        reservation.CancellationReason.ShouldBe("Buyer withdrew");

        (await UnitStateAsync()).ShouldBe(UnitState.Available);
    }

    [Fact]
    public async Task A_cancelled_unit_can_be_reserved_again()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        await MoveAsync(id, ReservationState.Cancelled, "Changed their mind");

        // The filtered unique index has to let a live reservation sit beside a cancelled one.
        await HoldItAsync();

        await using var db = fixture.CreateContext();
        (await Service(db).CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Created>();
    }

    [Fact]
    public async Task Every_move_is_audited()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        await MoveAsync(id, ReservationState.FeePaid);

        await using var db = fixture.CreateContext();
        var actions = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == id)
            .Select(a => a.Action)
            .ToListAsync();

        actions.ShouldBe(["reservation.created", "reservation.moved"], ignoreOrder: true);
    }

    // --- Editing --------------------------------------------------------------------

    [Fact]
    public async Task The_paperwork_fields_can_be_corrected()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            id,
            new ReservationEdit("CTR-2026-014", "Notariat Unirii", null, "Paying cash", null),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<ReservationOutcome.Updated>()
            .Changes.Keys.ShouldBe(["ContractNumber", "NotaryLocation", "InternalNotes"], ignoreOrder: true);

        (await LoadAsync(id)).ContractNumber.ShouldBe("CTR-2026-014");
    }

    [Fact]
    public async Task Changing_the_buyer_type_before_anything_is_agreed_redoes_the_price()
    {
        await fixture.ResetUnitAsync();

        await using (var db = fixture.CreateContext())
        {
            // A reduced rate only reaches the price when the unit qualifies for it.
            await db.Units.Where(u => u.Id == fixture.UnitId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.ReducedVatEligible, true));

            await db.PricingRules.ExecuteUpdateAsync(s => s.SetProperty(r => r.VatReduced, 0.09m));
        }

        var id = await ReserveAsync();
        var asIndividual = await LoadAsync(id);
        asIndividual.SnapVatRate.ShouldBe(0.09m);

        await using var context = fixture.CreateContext();
        (await Service(context).EditAsync(
                id, new ReservationEdit(null, null, null, null, BuyerType.Company),
                Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Updated>();

        var asCompany = await LoadAsync(id);
        asCompany.BuyerType.ShouldBe(BuyerType.Company);
        asCompany.SnapVatRate.ShouldBe(0.19m);
        asCompany.SnapTotalGross.ShouldBeGreaterThan(asIndividual.SnapTotalGross);
    }

    [Fact]
    public async Task Changing_the_buyer_type_after_the_fee_is_paid_is_refused()
    {
        await fixture.ResetUnitAsync();
        var id = await ReserveAsync();

        await MoveAsync(id, ReservationState.FeePaid);

        // The VAT rate is inside the frozen price. Changing it here would leave the snapshot
        // describing a schedule this buyer was never offered.
        await using var db = fixture.CreateContext();
        (await Service(db).EditAsync(
                id, new ReservationEdit(null, null, null, null, BuyerType.Company),
                Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Refused>()
            .Reason.ShouldContain("VAT");
    }
}
