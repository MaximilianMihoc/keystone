using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Hubix.Infrastructure.Admin;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Hubix.IntegrationTests;

/// <summary>
/// Narrowing the audit log.
/// </summary>
/// <remarks>
/// The filters are the whole of this feature — a log nobody can search is the SQL prompt the
/// runbook already tells people to use. Seven of them combine, so they are worth holding to
/// account against a real database rather than trusting them to read correctly.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class AuditSearchTests(DatabaseFixture fixture)
{
    private static AuditService Service(HubixDbContext db) => new(db);

    private static readonly DateTimeOffset Day = new(2026, 3, 4, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Lays down a small log with known shapes. CreatedAt is set explicitly rather than left to
    /// SaveChanges, because half these tests are about the date filter.
    /// </summary>
    private async Task SeedAsync()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();

        (AuditLog Row, DateTimeOffset At)[] rows =
        [
            (Row("reservation.created", "reservation", "staff", "ana@hubix.invalid"), Day),
            (Row("reservation.cancelled", "reservation", "staff", "ana@hubix.invalid"), Day.AddHours(1)),
            (Row("reservation.moved", "reservation", "staff", "bogdan@hubix.invalid"), Day.AddDays(1)),
            (Row("unit.edited", "unit", "staff", "bogdan@hubix.invalid"), Day.AddDays(2)),
            (Row("hold.released.expired", "hold", "system", null), Day.AddDays(3))
        ];

        db.AuditLogs.AddRange(rows.Select(r => r.Row));
        await db.SaveChangesAsync();

        // HubixDbContext.SaveChanges stamps CreatedAt with the clock, so the dates the range
        // tests need have to be written afterwards rather than set on the entity.
        foreach (var (row, at) in rows)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE [AuditLogs] SET CreatedAt = {at} WHERE Id = {row.Id}");
        }
    }

    private static AuditLog Row(
        string action, string entityType, string actorType, string? actor) =>
        new()
        {
            ActorType = actorType,
            ActorLabel = actor,
            EntityType = entityType,
            EntityId = Guid.CreateVersion7(),
            Action = action
        };

    [Fact]
    public async Task An_unfiltered_search_returns_everything_newest_first()
    {
        await SeedAsync();

        await using var db = fixture.CreateContext();
        var rows = await Service(db).SearchAsync(new AuditFilter(), CancellationToken.None);

        rows.Count.ShouldBe(5);
        rows[0].Action.ShouldBe("hold.released.expired");
        rows[^1].Action.ShouldBe("reservation.created");
    }

    [Fact]
    public async Task An_action_ending_in_a_dot_matches_the_whole_family()
    {
        await SeedAsync();

        await using var db = fixture.CreateContext();

        var family = await Service(db).SearchAsync(
            new AuditFilter(Action: "reservation."), CancellationToken.None);

        family.Count.ShouldBe(3);
        family.ShouldAllBe(r => r.Action.StartsWith("reservation."));

        // Without the dot it is one exact action, not a prefix.
        var one = await Service(db).SearchAsync(
            new AuditFilter(Action: "reservation.moved"), CancellationToken.None);

        one.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Filters_combine()
    {
        await SeedAsync();

        await using var db = fixture.CreateContext();

        var rows = await Service(db).SearchAsync(
            new AuditFilter(EntityType: "reservation", Actor: "ana"), CancellationToken.None);

        // Ana touched two reservations; Bogdan's reservation row and both non-reservation
        // rows have to fall out.
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.Actor == "ana@hubix.invalid");
    }

    [Fact]
    public async Task An_actor_is_matched_on_a_fragment()
    {
        await SeedAsync();

        await using var db = fixture.CreateContext();

        var rows = await Service(db).SearchAsync(
            new AuditFilter(Actor: "BOGDAN"), CancellationToken.None);

        // Case-insensitive, because the collation is, and nobody types an address exactly.
        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_date_range_takes_from_inclusive_and_to_exclusive()
    {
        await SeedAsync();

        await using var db = fixture.CreateContext();

        var rows = await Service(db).SearchAsync(
            new AuditFilter(From: Day.AddDays(1), To: Day.AddDays(3)), CancellationToken.None);

        // Day+1 and Day+2 are in; Day+3 is not, so a day filter can be written as
        // From = the day, To = the next one without double-counting the boundary.
        rows.Count.ShouldBe(2);
        rows.Select(r => r.Action).ShouldBe(["unit.edited", "reservation.moved"]);
    }

    [Fact]
    public async Task A_system_actor_is_findable_although_it_has_no_label()
    {
        await SeedAsync();

        await using var db = fixture.CreateContext();

        var rows = await Service(db).SearchAsync(
            new AuditFilter(ActorType: "system"), CancellationToken.None);

        rows.Count.ShouldBe(1);

        // The label falls back to the actor type, so the column is never blank.
        rows[0].Actor.ShouldBe("system");
    }

    [Fact]
    public async Task Take_is_bounded_at_both_ends()
    {
        await SeedAsync();

        await using var db = fixture.CreateContext();
        var service = Service(db);

        (await service.SearchAsync(new AuditFilter(Take: 2), CancellationToken.None))
            .Count.ShouldBe(2);

        // Zero and negative clamp up to one rather than returning nothing or throwing.
        (await service.SearchAsync(new AuditFilter(Take: 0), CancellationToken.None))
            .Count.ShouldBe(1);

        (await service.SearchAsync(new AuditFilter(Take: -5), CancellationToken.None))
            .Count.ShouldBe(1);

        // Above the ceiling is allowed, it just does not exceed it.
        (await service.SearchAsync(new AuditFilter(Take: 99999), CancellationToken.None))
            .Count.ShouldBe(5);
    }

    [Fact]
    public async Task The_kinds_are_read_from_the_rows_that_are_there()
    {
        await SeedAsync();

        await using var db = fixture.CreateContext();
        var kinds = await Service(db).KindsAsync(CancellationToken.None);

        kinds.EntityTypes.ShouldBe(["hold", "reservation", "unit"]);
        kinds.ActorTypes.ShouldBe(["staff", "system"]);

        // Distinct and sorted, so a dropdown can use it as it comes.
        kinds.Actions.Count.ShouldBe(5);
        kinds.Actions.ShouldContain(new AuditActionKind("reservation", "reservation.cancelled"));
    }

    [Fact]
    public async Task An_action_is_paired_with_its_entity_type_rather_than_its_prefix()
    {
        await fixture.ResetUnitAsync();

        await using (var seed = fixture.CreateContext())
        {
            // The real log does this: floor.created, pricing.created and translation.saved are
            // all filed under `project`. A dropdown that narrowed on the action's prefix would
            // hide every one of them the moment somebody filtered to project.
            seed.AuditLogs.Add(Row("floor.created", "project", "staff", "ana@hubix.invalid"));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var kinds = await Service(db).KindsAsync(CancellationToken.None);

        kinds.Actions.ShouldBe([new AuditActionKind("project", "floor.created")]);
    }
}
