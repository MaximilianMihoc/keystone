using System.Data.Common;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// A move that was decided on a stale read must not land on top of somebody else's work.
/// </summary>
/// <remarks>
/// <see cref="ReservationService.MoveAsync"/> reads the reservation before it opens its
/// transaction and saves it through the change tracker. Without a concurrency token that save
/// overwrote whatever happened in between — and a cancellation landing in that window produced a
/// row that was <c>FeePaid</c> and cancelled at the same time. Such a row slips past
/// <c>UX_Reservations_OneOpenPerUnit</c>, whose filter excludes anything cancelled, so the unit
/// ended up with two reservations that every "not cancelled" query counts (issue #36).
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class ReservationConcurrencyTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static ReservationService Service(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<ReservationService>.Instance);

    private static HoldService Holds(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<HoldService>.Instance);

    private static ProspectDetails Buyer(string email) =>
        new(email, "Ion", "Popescu", null, null, null, Cultures.Romanian, true, "test");

    /// <summary>Holds the fixture's unit and converts it, as the board does.</summary>
    private async Task<Guid> ReserveAsync(string email)
    {
        await using (var holding = fixture.CreateContext())
        {
            (await Holds(holding).TryHoldAsync(
                    fixture.UnitId, Buyer(email), null, CancellationToken.None))
                .ShouldBeOfType<HoldOutcome.Created>();
        }

        await using var db = fixture.CreateContext();

        return (await Service(db).CreateAsync(
                fixture.UnitId, null, BuyerType.Individual, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<ReservationOutcome.Created>()
            .ReservationId;
    }

    private async Task<ReservationOutcome> MoveAsync(
        Guid id, ReservationState target, string? reason = null)
    {
        await using var db = fixture.CreateContext();
        return await Service(db).MoveAsync(id, target, reason, Actor, "dev", CancellationToken.None);
    }

    private async Task<int> OpenReservationsOnTheUnitAsync()
    {
        await using var db = fixture.CreateContext();

        // The question the pipeline board, the units grid and CreateAsync all ask.
        return await db.Reservations
            .AsNoTracking()
            .CountAsync(r => r.UnitId == fixture.UnitId && r.State != ReservationState.Cancelled);
    }

    /// <summary>
    /// Sets the scene: a reservation read, then cancelled and replaced behind that read's back.
    /// </summary>
    /// <returns>The context holding the stale entity, and the entity itself.</returns>
    private async Task<(HubixDbContext Context, Reservation Stale)> StaleReadAsync()
    {
        var first = await ReserveAsync("first@example.ro");

        // Exactly the read MoveAsync takes before it opens its transaction.
        var context = fixture.CreateContext();
        var stale = await context.Reservations.SingleAsync(r => r.Id == first);

        // Meanwhile the sale falls through, the unit goes back on the market, and somebody
        // else buys it.
        (await MoveAsync(first, ReservationState.Cancelled, "buyer withdrew"))
            .ShouldBeOfType<ReservationOutcome.Moved>();

        await ReserveAsync("second@example.ro");

        return (context, stale);
    }

    /// <summary>
    /// Runs <paramref name="interfere"/> the moment <c>MoveAsync</c> has read the reservation.
    /// </summary>
    /// <remarks>
    /// That read happens before the transaction opens, which is both where the window is and the
    /// only point where another connection can still get in — once the transaction is running it
    /// holds the unit row and anything else would simply block. Interception rather than two
    /// racing threads because the test has to fail the same way every time.
    /// </remarks>
    private sealed class InterfereAfterTheRead(Func<Task> interfere) : DbCommandInterceptor
    {
        private bool done;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!done && command.CommandText.Contains("FROM [Reservations]", StringComparison.Ordinal))
            {
                done = true;
                await interfere();
            }

            return result;
        }
    }

    private HubixDbContext CreateInterferingContext(Func<Task> interfere) =>
        new(new DbContextOptionsBuilder<HubixDbContext>()
            .UseSqlServer(fixture.ConnectionString, sql =>
                sql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(5), null))
            .AddInterceptors(new InterfereAfterTheRead(interfere))
            .Options);

    [Fact]
    public async Task A_move_that_loses_the_race_is_refused_in_words_the_board_can_show()
    {
        await fixture.ResetUnitAsync();

        var reservationId = await ReserveAsync("first@example.ro");

        // Somebody edits the paperwork in the window between this move's read and its save.
        // An edit rather than a cancellation on purpose: it leaves the unit alone, so nothing
        // but the concurrency check can refuse what follows.
        async Task EditItAsync()
        {
            await using var other = fixture.CreateContext();

            (await Service(other).EditAsync(
                    reservationId,
                    new ReservationEdit("CTR-1234", null, null, null, null),
                    Actor, "colleague", CancellationToken.None))
                .ShouldBeOfType<ReservationOutcome.Updated>();
        }

        await using var db = CreateInterferingContext(EditItAsync);

        var outcome = await Service(db).MoveAsync(
            reservationId, ReservationState.FeePaid, null, Actor, "dev", CancellationToken.None);

        // Refused, not thrown, and not silently applied over the colleague's edit. The exact
        // wording matters here: the unit-state guard refuses with "Reload and try again" too,
        // so a looser assertion would pass without this path ever running.
        outcome.ShouldBeOfType<ReservationOutcome.Refused>()
            .Reason.ShouldStartWith("The reservation on");

        await using var verify = fixture.CreateContext();
        var reservation = await verify.Reservations.AsNoTracking()
            .SingleAsync(r => r.Id == reservationId);

        reservation.State.ShouldBe(ReservationState.PendingFee);   // the move did not land
        reservation.ContractNumber.ShouldBe("CTR-1234");           // the edit survived
    }

    [Fact]
    public async Task A_stale_move_is_refused_by_the_database_rather_than_winning()
    {
        await fixture.ResetUnitAsync();

        var (context, stale) = await StaleReadAsync();

        await using (context)
        {
            // The tail of MoveAsync, resumed on a reservation that no longer looks like this.
            stale.State = ReservationState.FeePaid;

            await Should.ThrowAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task The_database_refuses_a_row_that_is_open_and_cancelled_at_once()
    {
        await fixture.ResetUnitAsync();

        var reservationId = await ReserveAsync("first@example.ro");

        await using var db = fixture.CreateContext();

        // Exactly the row the stale save used to produce, written here deliberately. Nothing in
        // the application should ever ask for it, which is why the constraint is the right place
        // to say no: it holds for a hand-written UPDATE too.
        var write = () => db.Database.ExecuteSqlRawAsync(
            "UPDATE [Reservations] SET [CancelledAt] = SYSDATETIMEOFFSET() WHERE [Id] = {0}",
            reservationId);

        (await Should.ThrowAsync<Exception>(write))
            .Message.ShouldContain("CK_Reservations_CancelledAgrees");
    }

    [Fact]
    public async Task A_stale_move_cannot_leave_a_unit_with_two_open_reservations()
    {
        await fixture.ResetUnitAsync();

        var (context, stale) = await StaleReadAsync();

        await using (context)
        {
            stale.State = ReservationState.FeePaid;

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // What is supposed to happen. The count below is the point of the test.
            }
        }

        // Two would mean the cancelled row came back to life as FeePaid while keeping its
        // CancelledAt, which the unique index does not look at and the board would draw as
        // one apartment sold twice.
        (await OpenReservationsOnTheUnitAsync()).ShouldBe(1);
    }
}
