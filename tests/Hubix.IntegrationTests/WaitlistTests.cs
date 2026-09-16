using System.Text.Json;
using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// The queue that replaces a separate Google Form per apartment type.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class WaitlistTests(DatabaseFixture fixture)
{
    private static WaitlistService Service(HubixDbContext db) =>
        new(db, TimeProvider.System, NullLogger<WaitlistService>.Instance);

    private static ProspectDetails Person(string email) =>
        new(email, "Waiting", "Person", null, null, null, Cultures.Romanian, true, "waitlist-form/test");

    private async Task<Guid> TypeIdAsync()
    {
        await using var db = fixture.CreateContext();
        // By name, not "the first one": the catalogue is portal-wide, so another test's type
        // could otherwise be picked up here.
        return await db.UnitTypes.Where(t => t.Name == "2 camere")
            .Select(t => t.Id).SingleAsync();
    }

    private async Task<WaitlistOutcome.Joined> JoinAsync(string email, Guid? typeId)
    {
        await using var db = fixture.CreateContext();

        return (await Service(db).JoinAsync(fixture.ProjectId, typeId, Person(email), CancellationToken.None))
            .ShouldBeOfType<WaitlistOutcome.Joined>();
    }

    private async Task<string> TokenOfAsync(Guid entryId)
    {
        await using var db = fixture.CreateContext();
        return await db.WaitlistEntries.Where(w => w.Id == entryId).Select(w => w.AccessToken).SingleAsync();
    }

    [Fact]
    public async Task Joining_puts_somebody_at_the_back_of_the_queue()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        (await JoinAsync("first@example.ro", type)).Place.ShouldBe(1);
        (await JoinAsync("second@example.ro", type)).Place.ShouldBe(2);
        (await JoinAsync("third@example.ro", type)).Place.ShouldBe(3);
    }

    [Fact]
    public async Task The_place_shown_falls_when_somebody_ahead_leaves()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        var first = await JoinAsync("first@example.ro", type);
        await JoinAsync("second@example.ro", type);
        var third = await JoinAsync("third@example.ro", type);

        await using var db = fixture.CreateContext();
        (await Service(db).LeaveAsync(await TokenOfAsync(first.EntryId), CancellationToken.None)).ShouldBeTrue();

        // The stored arrival number does not move; what somebody is told does. A place that
        // was written down once would still be claiming third long after they became second.
        (await Service(db).PlaceOfAsync(third.EntryId, CancellationToken.None)).ShouldBe(2);
    }

    [Fact]
    public async Task Joining_twice_gives_back_the_same_place_rather_than_failing()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        var first = await JoinAsync("keen@example.ro", type);

        await using var db = fixture.CreateContext();
        var again = (await Service(db).JoinAsync(
                fixture.ProjectId, type, Person("keen@example.ro"), CancellationToken.None))
            .ShouldBeOfType<WaitlistOutcome.AlreadyWaiting>();

        again.EntryId.ShouldBe(first.EntryId);
        again.Place.ShouldBe(1);
    }

    [Fact]
    public async Task Joining_with_somebody_elses_address_does_not_hand_over_their_link()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        var victim = await JoinAsync("victim@example.ro", type);
        var token = await TokenOfAsync(victim.EntryId);

        // Anybody can type an address into a public form. The token is the whole credential
        // for an entry, so what comes back must not contain it, or knowing an address would
        // be enough to take somebody off the list (issue #29).
        await using var db = fixture.CreateContext();
        var outcome = await Service(db).JoinAsync(
            fixture.ProjectId, type, Person("victim@example.ro"), CancellationToken.None);

        outcome.ShouldBeOfType<WaitlistOutcome.AlreadyWaiting>();
        JsonSerializer.Serialize<object>(outcome).ShouldNotContain(token);
    }

    [Fact]
    public async Task Somebody_may_queue_for_one_type_and_for_anything_at_once()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        // Two different questions — "I want a two-bed" and "I'll take whatever comes up" —
        // so the unique index is per queue, not per person.
        (await JoinAsync("flexible@example.ro", type)).ShouldNotBeNull();
        (await JoinAsync("flexible@example.ro", null)).ShouldNotBeNull();

        await using var db = fixture.CreateContext();
        (await db.WaitlistEntries.CountAsync(w => w.RemovedAt == null)).ShouldBe(2);
    }

    [Fact]
    public async Task Leaving_is_idempotent()
    {
        await fixture.ResetUnitAsync();
        var joined = await JoinAsync("gone@example.ro", null);
        var token = await TokenOfAsync(joined.EntryId);

        await using var db = fixture.CreateContext();

        (await Service(db).LeaveAsync(token, CancellationToken.None)).ShouldBeTrue();
        (await Service(db).LeaveAsync(token, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_token_finds_nothing()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        (await Service(db).FindByTokenAsync("not-a-real-token", CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task The_raw_token_is_what_reaches_the_queue_and_it_is_random()
    {
        await fixture.ResetUnitAsync();

        var one = await JoinAsync("one@example.ro", null);
        var oneToken = await TokenOfAsync(one.EntryId);
        await using var db = fixture.CreateContext();
        (await Service(db).LeaveAsync(oneToken, CancellationToken.None)).ShouldBeTrue();

        var two = await JoinAsync("two@example.ro", null);
        var twoToken = await TokenOfAsync(two.EntryId);

        // Not derived from the id: those are UUIDv7 and time-ordered, so a neighbour could
        // guess at them.
        twoToken.ShouldNotBe(oneToken);
        twoToken.Length.ShouldBeGreaterThan(40);
        twoToken.ShouldNotContain(two.EntryId.ToString());
    }

    [Fact]
    public async Task Consent_is_recorded_for_the_waiting_list_specifically()
    {
        await fixture.ResetUnitAsync();
        await JoinAsync("consenting@example.ro", null);

        await using var db = fixture.CreateContext();
        var prospect = await db.Prospects.AsNoTracking()
            .SingleAsync(p => p.Email == "consenting@example.ro");

        // Not folded into whatever they may have agreed to when reserving. "Used only for its
        // stated purpose" has to mean this purpose.
        prospect.GdprConsentPurpose.ShouldContain("waitlist");
        prospect.GdprConsentAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_unpublished_project_has_no_queue_to_join()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        await db.Projects.Where(p => p.Id == fixture.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PublishedAt, (DateTimeOffset?)null));

        var result = await Service(db).JoinAsync(
            fixture.ProjectId, null, Person("early@example.ro"), CancellationToken.None);

        result.ShouldBeOfType<WaitlistOutcome.ProjectNotFound>();

        await db.Projects.Where(p => p.Id == fixture.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PublishedAt, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_type_that_does_not_exist_is_refused()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        (await Service(db).JoinAsync(
                fixture.ProjectId, Guid.CreateVersion7(), Person("confused@example.ro"), CancellationToken.None))
            .ShouldBeOfType<WaitlistOutcome.UnknownType>();
    }

    // --- Who gets told ---------------------------------------------------------------

    [Fact]
    public async Task Somebody_waiting_for_the_exact_type_beats_somebody_waiting_for_anything()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        // The flexible one joined first and still loses: a better match is a better match.
        await JoinAsync("anything@example.ro", null);
        await JoinAsync("exactly@example.ro", type);

        await using var db = fixture.CreateContext();
        var next = await Service(db).NextInLineAsync(fixture.ProjectId, type, CancellationToken.None);

        next.ShouldNotBeNull();
        next.Email.ShouldBe("exactly@example.ro");
    }

    [Fact]
    public async Task Somebody_already_told_is_not_told_again()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        var first = await JoinAsync("first@example.ro", type);
        await JoinAsync("second@example.ro", type);

        await using var db = fixture.CreateContext();
        var service = Service(db);

        await service.MarkNotifiedAsync(first.EntryId, CancellationToken.None);

        var next = await service.NextInLineAsync(fixture.ProjectId, type, CancellationToken.None);
        next!.Email.ShouldBe("second@example.ro");
    }

    [Fact]
    public async Task Somebody_who_left_is_never_told()
    {
        await fixture.ResetUnitAsync();
        var type = await TypeIdAsync();

        var first = await JoinAsync("left@example.ro", type);
        await JoinAsync("stayed@example.ro", type);

        await using var db = fixture.CreateContext();
        var service = Service(db);

        await service.LeaveAsync(await TokenOfAsync(first.EntryId), CancellationToken.None);

        var next = await service.NextInLineAsync(fixture.ProjectId, type, CancellationToken.None);
        next!.Email.ShouldBe("stayed@example.ro");
    }

    [Fact]
    public async Task An_empty_queue_has_nobody_to_tell()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        (await Service(db).NextInLineAsync(fixture.ProjectId, null, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Staff_removing_somebody_writes_an_audit_row()
    {
        await fixture.ResetUnitAsync();
        var joined = await JoinAsync("removed@example.ro", null);

        await using var db = fixture.CreateContext();
        var actor = Guid.CreateVersion7();

        (await Service(db).RemoveAsync(joined.EntryId, actor, "dev@hubix.invalid", CancellationToken.None))
            .ShouldBeTrue();

        var audit = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.EntityId == joined.EntryId);

        audit.Action.ShouldBe("waitlist.removed");
        audit.ActorLabel.ShouldBe("dev@hubix.invalid");
    }
}
