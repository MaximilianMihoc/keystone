using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Admin;
using Keystone.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// The pipeline against a real database: the unit follows the reservation, the agreed price
/// does not move, and two people cannot sell the same apartment.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class ReservationTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static ReservationService Service(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<ReservationService>.Instance);

    private static HoldService Holds(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<HoldService>.Instance);

    private static ProspectDetails Buyer(string email = "buyer@example.ro") =>
        new(email, "Ion", "Popescu", null, null, null, Cultures.Romanian, true, "test");

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
            // The operator raises prices mid-launch; the Lake Home document describes exactly this.
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

    [Fact]
    public async Task Parking_held_with_the_apartment_comes_with_the_reservation()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 3, maxPerReservation: 2);

        await using (var holding = fixture.CreateContext())
        {
            (await Holds(holding).TryHoldAsync(
                    fixture.UnitId,
                    Buyer(),
                    new ParkingRequest(ParkingSpaceKind.Underground, 2),
                    null,
                    CancellationToken.None))
                .ShouldBeOfType<HoldOutcome.Created>();
        }

        await using var db = fixture.CreateContext();

        var created = (await Service(db).CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Created>();

        await using var verify = fixture.CreateContext();

        var reservation = await verify.Reservations
            .SingleAsync(r => r.Id == created.ReservationId);

        // Two spaces at 8,000 each, snapshotted rather than looked up later.
        reservation.SnapParkingNet.ShouldBe(16_000m);
        reservation.SnapParkingDepositNet.ShouldBe(4_352m);

        var spaces = await verify.ParkingSpaces
            .Where(s => s.ReservationId == created.ReservationId)
            .ToListAsync();

        spaces.Count.ShouldBe(2);
        spaces.ShouldAllBe(s => s.State == UnitState.Reserved);

        // The hold link is dropped: the sale owns them now, and a space pointing at both
        // would be two answers to who has it.
        spaces.ShouldAllBe(s => s.HoldId == null);
    }

    [Fact]
    public async Task Repricing_the_car_park_does_not_move_an_agreed_reservation()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 1, maxPerReservation: 1);

        await using (var holding = fixture.CreateContext())
        {
            await Holds(holding).TryHoldAsync(
                fixture.UnitId,
                Buyer(),
                new ParkingRequest(ParkingSpaceKind.Underground, 1),
                null,
                CancellationToken.None);
        }

        Guid reservationId;
        await using (var db = fixture.CreateContext())
        {
            reservationId = ((ReservationOutcome.Created)await Service(db).CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
                .ReservationId;
        }

        await using (var reprice = fixture.CreateContext())
        {
            await reprice.ParkingPrices
                .Where(p => p.ProjectId == fixture.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceNet, 12_000m));
        }

        await using var verify = fixture.CreateContext();

        // What they agreed to, not what it costs today.
        (await verify.Reservations.SingleAsync(r => r.Id == reservationId))
            .SnapParkingNet.ShouldBe(8_000m);
    }

    [Fact]
    public async Task A_space_with_no_price_refuses_the_reservation_rather_than_selling_it_for_nothing()
    {
        await fixture.ResetUnitAsync();
        await fixture.SeedParkingAsync(spaces: 1, maxPerReservation: 1);

        await using (var holding = fixture.CreateContext())
        {
            await Holds(holding).TryHoldAsync(
                fixture.UnitId,
                Buyer(),
                new ParkingRequest(ParkingSpaceKind.Underground, 1),
                null,
                CancellationToken.None);
        }

        // The price disappears between the hold and the conversion.
        await using (var drop = fixture.CreateContext())
        {
            await drop.ParkingPrices
                .Where(p => p.ProjectId == fixture.ProjectId)
                .ExecuteDeleteAsync();
        }

        await using var db = fixture.CreateContext();

        (await Service(db).CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Refused>()
            .Reason.ShouldContain("price");

        // Refused means nothing moved: no reservation, and the unit is as it was.
        await using var verify = fixture.CreateContext();
        (await verify.Reservations.CountAsync()).ShouldBe(0);
    }

    /// <summary>Reserves the fixture's unit with one space and returns both ids.</summary>
    private async Task<(Guid ReservationId, Guid SpaceId)> ReserveWithParkingAsync()
    {
        await fixture.SeedParkingAsync(spaces: 3, maxPerReservation: 2);

        await using (var holding = fixture.CreateContext())
        {
            await Holds(holding).TryHoldAsync(
                fixture.UnitId, Buyer(), new ParkingRequest(ParkingSpaceKind.Underground, 1),
                null, CancellationToken.None);
        }

        await using var db = fixture.CreateContext();

        var created = (ReservationOutcome.Created)await Service(db).CreateAsync(
            fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None);

        var spaceId = await db.ParkingSpaces
            .Where(s => s.ReservationId == created.ReservationId)
            .Select(s => s.Id)
            .SingleAsync();

        return (created.ReservationId, spaceId);
    }

    [Fact]
    public async Task A_buyer_can_add_parking_after_reserving_at_todays_price()
    {
        await fixture.ResetUnitAsync();
        var (reservationId, _) = await ReserveWithParkingAsync();

        // The car park is repriced after they reserved. A space added now is agreed now, so it
        // takes the new price; the one they already have keeps the old one.
        await using (var reprice = fixture.CreateContext())
        {
            await reprice.ParkingPrices
                .Where(p => p.ProjectId == fixture.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceNet, 12_000m));
        }

        Guid freeSpaceId;
        await using (var db = fixture.CreateContext())
        {
            freeSpaceId = await db.ParkingSpaces
                .Where(s => s.State == UnitState.Available)
                .OrderBy(s => s.Code)
                .Select(s => s.Id)
                .FirstAsync();

            (await Service(db).AddParkingAsync(
                    reservationId, freeSpaceId, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Updated>();
        }

        await using var verify = fixture.CreateContext();

        // 8,000 agreed in January plus 12,000 agreed today.
        (await verify.Reservations.SingleAsync(r => r.Id == reservationId))
            .SnapParkingNet.ShouldBe(20_000m);

        var added = await verify.ParkingSpaces.SingleAsync(s => s.Id == freeSpaceId);
        added.SnapPriceNet.ShouldBe(12_000m);
        added.State.ShouldBe(UnitState.Reserved);
    }

    [Fact]
    public async Task Removing_parking_subtracts_what_that_space_cost_not_a_share_of_the_total()
    {
        await fixture.ResetUnitAsync();
        var (reservationId, spaceId) = await ReserveWithParkingAsync();

        // A second space at a different price, so a naive "divide the total" would be wrong.
        await using (var reprice = fixture.CreateContext())
        {
            await reprice.ParkingPrices
                .Where(p => p.ProjectId == fixture.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceNet, 12_000m));
        }

        await using (var db = fixture.CreateContext())
        {
            var free = await db.ParkingSpaces
                .Where(s => s.State == UnitState.Available)
                .OrderBy(s => s.Code)
                .Select(s => s.Id)
                .FirstAsync();

            await Service(db).AddParkingAsync(reservationId, free, Actor, "dev", CancellationToken.None);
        }

        // Drop the original 8,000 one. What is left must be exactly the 12,000 one.
        await using (var db = fixture.CreateContext())
        {
            (await Service(db).RemoveParkingAsync(
                    reservationId, spaceId, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Updated>();
        }

        await using var verify = fixture.CreateContext();

        (await verify.Reservations.SingleAsync(r => r.Id == reservationId))
            .SnapParkingNet.ShouldBe(12_000m);

        // And the dropped bay is back on the market with no agreed price hanging off it.
        var freed = await verify.ParkingSpaces.SingleAsync(s => s.Id == spaceId);
        freed.State.ShouldBe(UnitState.Available);
        freed.ReservationId.ShouldBeNull();
        freed.SnapPriceNet.ShouldBeNull();
    }

    [Fact]
    public async Task Parking_is_locked_once_the_cesiune_is_signed()
    {
        await fixture.ResetUnitAsync();
        var (reservationId, spaceId) = await ReserveWithParkingAsync();

        foreach (var stage in new[]
                 {
                     ReservationState.FeePaid,
                     ReservationState.AgreementSent,
                     ReservationState.AgreementSigned,
                     ReservationState.NotaryScheduled
                 })
        {
            if (stage is ReservationState.NotaryScheduled)
            {
                await using var setDate = fixture.CreateContext();
                await setDate.Reservations
                    .Where(r => r.Id == reservationId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.NotaryAppointmentAt, DateTimeOffset.UtcNow.AddDays(7))
                        .SetProperty(r => r.NotaryLocation, "Notary"));
            }

            (await MoveAsync(reservationId, stage)).ShouldBeOfType<ReservationOutcome.Moved>();
        }

        // Still open one stage before the signing: a notary date is often weeks away, and
        // buyers change their minds in that window.
        await using (var stillOpen = fixture.CreateContext())
        {
            var free = await stillOpen.ParkingSpaces
                .Where(s => s.State == UnitState.Available)
                .Select(s => s.Id)
                .FirstAsync();

            (await Service(stillOpen).AddParkingAsync(
                    reservationId, free, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Updated>();
        }

        (await MoveAsync(reservationId, ReservationState.CesiuneSigned))
            .ShouldBeOfType<ReservationOutcome.Moved>();

        // From here the deed names the space, so the record must not move behind it.
        await using var db = fixture.CreateContext();

        (await Service(db).RemoveParkingAsync(
                reservationId, spaceId, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Refused>()
            .Reason.ShouldContain("notary");
    }
    [Fact]
    public async Task A_unit_marked_reserved_elsewhere_can_start_a_pipeline()
    {
        await fixture.ResetUnitAsync();

        // How a sale made through an agent arrives: the unit is marked, nobody is named yet.
        await using (var mark = fixture.CreateContext())
        {
            (await new UnitAdminService(mark, TimeProvider.System, NullLogger<UnitAdminService>.Instance)
                    .SetReservedAsync(fixture.UnitId, true, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<UnitEditResult.Updated>();
        }

        // Later, with a buyer to name, the pipeline picks it up from there. No hold exists, so
        // the details are required and the unit stays where it already is.
        await using (var start = fixture.CreateContext())
        {
            (await Service(start).CreateAsync(
                    fixture.UnitId, Buyer(), BuyerType.Individual, Actor, "dev", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Created>();
        }

        (await UnitStateAsync()).ShouldBe(UnitState.Reserved);

        // And from that moment the mark is the pipeline's to undo, not the grid's.
        await using var db = fixture.CreateContext();
        var undo = await new UnitAdminService(db, TimeProvider.System, NullLogger<UnitAdminService>.Instance)
            .SetReservedAsync(fixture.UnitId, false, Actor, "dev", CancellationToken.None);

        undo.ShouldBeOfType<UnitEditResult.Rejected>().Reason.ShouldContain("pipeline");
    }
}
