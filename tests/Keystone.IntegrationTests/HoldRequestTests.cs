using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// Asking to reserve, and what an address typed into a public form may and may not do.
/// </summary>
/// <remarks>
/// Anybody can type anybody else's address into the reservation form. Until the link sent to
/// that address comes back, a submission must leave the apartment on sale and the record under
/// that address exactly as it was — no rewritten details, no consent, no block (issue #30).
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class HoldRequestTests(DatabaseFixture fixture)
{
    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);

    private HoldService Service(KeystoneDbContext db) =>
        new(db, _clock, NullLogger<HoldService>.Instance);

    private static ProspectDetails Someone(string email, string? phone = "0700000000") => new(
        Email: email,
        FirstName: "Typed",
        Surname: "In",
        Phone: phone,
        City: "Iasi",
        Country: null,
        Locale: Cultures.Romanian,
        ConsentGiven: true,
        ConsentSource: "reserve-form/test");

    /// <summary>A buyer who is already on file, as anyone who has reserved before would be.</summary>
    private async Task<Guid> ExistingBuyerAsync()
    {
        await using var db = fixture.CreateContext();

        var buyer = new Prospect
        {
            Email = "buyer@example.ro",
            FirstName = "Real",
            Surname = "Buyer",
            Phone = "0722222222",
            City = "Timisoara",
            Locale = Cultures.Romanian,
            Source = "website"
        };

        db.Prospects.Add(buyer);
        await db.SaveChangesAsync();

        return buyer.Id;
    }

    private async Task<string> RequestAsync(string email)
    {
        await using var db = fixture.CreateContext();

        return (await Service(db).RequestAsync(
                fixture.UnitId, Someone(email), null, "::1", CancellationToken.None))
            .ShouldBeOfType<HoldRequestOutcome.Recorded>().Token;
    }

    // --- What a submission may not do ------------------------------------------------

    [Fact]
    public async Task Asking_holds_nothing_and_touches_nobody()
    {
        await fixture.ResetUnitAsync();
        var buyerId = await ExistingBuyerAsync();

        await RequestAsync("buyer@example.ro");

        await using var db = fixture.CreateContext();

        // The apartment is still for sale: whoever typed that address has shown nothing yet.
        (await db.Units.Where(u => u.Id == fixture.UnitId).Select(u => u.State).SingleAsync())
            .ShouldBe(UnitState.Available);
        (await db.Holds.CountAsync()).ShouldBe(0);

        var buyer = await db.Prospects.AsNoTracking().SingleAsync(p => p.Id == buyerId);

        buyer.FirstName.ShouldBe("Real");
        buyer.Surname.ShouldBe("Buyer");
        buyer.Phone.ShouldBe("0722222222");
        buyer.City.ShouldBe("Timisoara");
        buyer.GdprConsentAt.ShouldBeNull();
        buyer.CanHold.ShouldBeTrue();
    }

    [Fact]
    public async Task Asking_under_an_unknown_address_writes_no_prospect_at_all()
    {
        await fixture.ResetUnitAsync();

        await RequestAsync("stranger@example.ro");

        await using var db = fixture.CreateContext();
        (await db.Prospects.CountAsync(p => p.Email == "stranger@example.ro")).ShouldBe(0);
    }

    [Fact]
    public async Task A_request_that_is_never_confirmed_cannot_block_the_buyer()
    {
        await fixture.ResetUnitAsync();
        var buyerId = await ExistingBuyerAsync();

        // The old shape of this attack: ask under somebody's address, then let it lapse. There
        // is nothing to lapse now, because nothing was held.
        await RequestAsync("buyer@example.ro");
        _clock.Advance(HoldRequest.Lifetime + TimeSpan.FromMinutes(1));

        await using var db = fixture.CreateContext();
        var buyer = await db.Prospects.AsNoTracking().SingleAsync(p => p.Id == buyerId);

        buyer.CanHold.ShouldBeTrue();
        buyer.LapsedHoldCount.ShouldBe(0);
    }

    // --- What the link does ----------------------------------------------------------

    [Fact]
    public async Task Following_the_link_holds_the_apartment()
    {
        await fixture.ResetUnitAsync();

        var token = await RequestAsync("keen@example.ro");

        await using var db = fixture.CreateContext();
        var created = (await Service(db).ConfirmAsync(token, "::1", CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.Created>();

        created.AccessToken.ShouldNotBe(token);

        (await db.Units.Where(u => u.Id == fixture.UnitId).Select(u => u.State).SingleAsync())
            .ShouldBe(UnitState.Held);

        // Now that the address is known to be theirs, what they typed is theirs to set.
        var prospect = await db.Prospects.AsNoTracking().SingleAsync(p => p.Email == "keen@example.ro");
        prospect.FirstName.ShouldBe("Typed");
        prospect.GdprConsentAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_link_works_once()
    {
        await fixture.ResetUnitAsync();

        var token = await RequestAsync("keen@example.ro");

        await using var db = fixture.CreateContext();
        var service = Service(db);

        (await service.ConfirmAsync(token, null, CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.Created>();

        (await service.ConfirmAsync(token, null, CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.LinkNotUsable>();
    }

    [Fact]
    public async Task A_link_stops_working_after_an_hour()
    {
        await fixture.ResetUnitAsync();

        var token = await RequestAsync("slow@example.ro");
        _clock.Advance(HoldRequest.Lifetime + TimeSpan.FromMinutes(1));

        await using var db = fixture.CreateContext();

        (await Service(db).ConfirmAsync(token, null, CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.LinkNotUsable>();

        (await db.Units.Where(u => u.Id == fixture.UnitId).Select(u => u.State).SingleAsync())
            .ShouldBe(UnitState.Available);
    }

    [Fact]
    public async Task A_token_nobody_issued_is_refused()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        (await Service(db).ConfirmAsync("not-a-real-token", null, CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.LinkNotUsable>();
    }

    [Fact]
    public async Task An_apartment_sold_while_the_email_sat_unread_is_gone()
    {
        await fixture.ResetUnitAsync();

        var token = await RequestAsync("slow@example.ro");

        await using var db = fixture.CreateContext();

        // Somebody else confirmed theirs first. The apartment was never off sale in the
        // meantime, which is the price of not holding it for an unproven address.
        (await Service(db).TryHoldAsync(
                fixture.UnitId, Someone("quicker@example.ro"), null, CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.Created>();

        (await Service(db).ConfirmAsync(token, null, CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.NoLongerAvailable>();
    }

    [Fact]
    public async Task A_blocked_buyer_is_only_told_at_the_link()
    {
        await fixture.ResetUnitAsync();
        var buyerId = await ExistingBuyerAsync();

        await using (var setup = fixture.CreateContext())
        {
            var blocked = await setup.Prospects.SingleAsync(p => p.Id == buyerId);
            blocked.Block("Test");
            await setup.SaveChangesAsync();
        }

        // Asking says nothing about them: this form must not answer questions about somebody
        // else's record to whoever typed their address.
        var token = await RequestAsync("buyer@example.ro");

        await using var db = fixture.CreateContext();

        (await Service(db).ConfirmAsync(token, null, CancellationToken.None))
            .ShouldBeOfType<HoldOutcome.ProspectBlocked>();
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
