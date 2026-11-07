using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// Creating and configuring projects, developers and their prose.
/// </summary>
/// <remarks>
/// Against a real database on purpose. Three of the rules under test are unique indexes rather
/// than C# — the project slug, the developer name, and one translation row per language — and
/// the in-memory provider enforces none of them, so the same tests there would pass while the
/// application failed.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class ProjectAdminTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private ProjectAdminService Service(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<ProjectAdminService>.Instance);

    private static DeveloperEdit Developer(string name = "Vertical Development") =>
        new(name, "Builds things", 12, 400, "https://example.invalid");

    private static ProjectCreate Create(string name = "Linden Park View", string? slug = null) =>
        new(name, "București", slug, null);

    private static TranslationEdit Prose(string summary) =>
        new(null, summary, null, null, null, null);

    private async Task<Guid> NewProjectAsync(string name = "Linden Park View")
    {
        await using var db = fixture.CreateContext();

        var result = await Service(db).CreateProjectAsync(
            Create(name), Actor, "dev@keystone.invalid", CancellationToken.None);

        return result.ShouldBeOfType<AdminWriteResult.Created>().Id;
    }

    private async Task UnpublishFixtureProjectAsync()
    {
        await using var db = fixture.CreateContext();

        await db.Projects
            .Where(p => p.Id == fixture.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PublishedAt, (DateTimeOffset?)null));
    }

    [Fact]
    public async Task A_project_is_created_as_an_unpublished_draft()
    {
        await fixture.ResetUnitAsync();

        var id = await NewProjectAsync();

        await using var verify = fixture.CreateContext();
        var project = await verify.Projects.SingleAsync(p => p.Id == id);

        project.Slug.ShouldBe("linden-park-view");
        project.Status.ShouldBe(ProjectStatus.Draft);
        project.PublishedAt.ShouldBeNull();

        // The gate every public page uses, asserted the way the pages themselves express it.
        (await verify.Projects.AnyAsync(p => p.Id == id && p.PublishedAt != null)).ShouldBeFalse();

        (await verify.AuditLogs.SingleAsync(a => a.Action == "project.created"))
            .ActorLabel.ShouldBe("dev@keystone.invalid");
    }

    [Fact]
    public async Task A_second_project_cannot_take_the_same_address()
    {
        await fixture.ResetUnitAsync();
        await NewProjectAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateProjectAsync(Create(), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("linden-park-view");
    }

    [Fact]
    public async Task Two_admins_creating_the_same_address_at_once_get_a_refusal_not_a_crash()
    {
        await fixture.ResetUnitAsync();

        // Both pass the pre-check before either writes, so one has to lose on the unique index.
        // This is the only test that reaches the DbUpdateException path, and without it that
        // branch ships never having run.
        var attempts = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return await Service(db).CreateProjectAsync(Create(), Actor, "dev", CancellationToken.None);
        });

        var results = await Task.WhenAll(attempts);

        results.Count(r => r is AdminWriteResult.Created).ShouldBe(1);
        results.Count(r => r is AdminWriteResult.Rejected).ShouldBe(1);
    }

    [Fact]
    public async Task Publishing_needs_at_least_one_apartment()
    {
        await fixture.ResetUnitAsync();
        var id = await NewProjectAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).SetPublishedAsync(id, true, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("no apartments");
    }

    [Fact]
    public async Task Publishing_moves_a_draft_on_to_upcoming()
    {
        await fixture.ResetUnitAsync();

        await using (var setup = fixture.CreateContext())
        {
            await setup.Projects
                .Where(p => p.Id == fixture.ProjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.PublishedAt, (DateTimeOffset?)null)
                    .SetProperty(p => p.Status, ProjectStatus.Draft));
        }

        await using var db = fixture.CreateContext();
        await Service(db).SetPublishedAsync(fixture.ProjectId, true, Actor, "dev", CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var project = await verify.Projects.SingleAsync(p => p.Id == fixture.ProjectId);

        project.PublishedAt.ShouldNotBeNull();
        project.Status.ShouldBe(ProjectStatus.Upcoming);
    }

    [Fact]
    public async Task Publishing_leaves_a_project_already_selling_where_it_is()
    {
        await fixture.ResetUnitAsync();
        await UnpublishFixtureProjectAsync();

        await using var db = fixture.CreateContext();
        await Service(db).SetPublishedAsync(fixture.ProjectId, true, Actor, "dev", CancellationToken.None);

        await using var verify = fixture.CreateContext();
        (await verify.Projects.SingleAsync(p => p.Id == fixture.ProjectId))
            .Status.ShouldBe(ProjectStatus.Selling);
    }

    [Fact]
    public async Task Unpublishing_hides_the_project_and_leaves_its_status_alone()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        await Service(db).SetPublishedAsync(fixture.ProjectId, false, Actor, "dev", CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var project = await verify.Projects.SingleAsync(p => p.Id == fixture.ProjectId);

        project.PublishedAt.ShouldBeNull();
        project.Status.ShouldBe(ProjectStatus.Selling);
    }

    [Fact]
    public async Task Reordering_numbers_the_projects_from_zero_in_the_order_given()
    {
        await fixture.ResetUnitAsync();

        var second = await NewProjectAsync("Second");
        var third = await NewProjectAsync("Third");

        // The whole list has to arrive, seeded project included, or the write is refused.
        var order = new[] { third, fixture.ProjectId, second };

        await using var db = fixture.CreateContext();
        (await Service(db).ReorderProjectsAsync(order, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Updated>();

        await using var verify = fixture.CreateContext();
        var stored = await verify.Projects
            .OrderBy(p => p.SortOrder)
            .Select(p => p.Id)
            .ToListAsync();

        stored.ShouldBe(order);

        // Dense from zero, so a project never ordered sorts like one that was.
        (await verify.Projects.OrderBy(p => p.SortOrder).Select(p => p.SortOrder).ToListAsync())
            .ShouldBe(new[] { 0, 1, 2 });

        // Dropping a row back where it started must not fill the audit log with empty rows.
        await using var again = fixture.CreateContext();
        (await Service(again).ReorderProjectsAsync(order, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.NoChange>();
    }

    [Fact]
    public async Task Reordering_a_list_that_has_lost_a_project_is_refused_rather_than_guessed_at()
    {
        await fixture.ResetUnitAsync();

        var second = await NewProjectAsync("Second");

        // A screen that loaded before the second project existed would send only the first.
        await using var db = fixture.CreateContext();
        var result = await Service(db).ReorderProjectsAsync(
            [fixture.ProjectId], Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("Reload");

        await using var verify = fixture.CreateContext();
        (await verify.Projects.SingleAsync(p => p.Id == second)).SortOrder.ShouldBe(0);
    }

    [Fact]
    public async Task A_published_project_cannot_change_its_address()
    {
        await fixture.ResetUnitAsync();

        var seeded = DatabaseFixture.SeedProject;

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditProjectAsync(
            fixture.ProjectId,
            new ProjectEdit(seeded.Name, seeded.City, "a-new-address", null, seeded.Status,
                1, null, 0, null, 4_000m, "EUR", 48, 3, null, null),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("web address cannot change");
    }

    [Fact]
    public async Task An_unpublished_project_can_be_renamed_and_moved()
    {
        await fixture.ResetUnitAsync();
        await UnpublishFixtureProjectAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditProjectAsync(
            fixture.ProjectId,
            new ProjectEdit("Linden Park View", "Brașov", "linden-brasov", null, ProjectStatus.Selling,
                20, new DateOnly(2027, 6, 30), 6, RentalProfile.ShortTerm, 5_000m, "EUR", 72, 5, null, null),
            Actor, "dev", CancellationToken.None);

        var updated = result.ShouldBeOfType<AdminWriteResult.Updated>();
        updated.Changes.Keys.ShouldContain("Slug");

        await using var verify = fixture.CreateContext();
        var project = await verify.Projects.SingleAsync(p => p.Id == fixture.ProjectId);

        project.Slug.ShouldBe("linden-brasov");
        project.City.ShouldBe("Brașov");
        project.DeliveryGraceMonths.ShouldBe(6);
        project.RentalProfile.ShouldBe(RentalProfile.ShortTerm);
        project.MaxActiveHoldsPerProspect.ShouldBe(5);
    }

    [Fact]
    public async Task An_empty_project_is_deleted_and_the_audit_row_outlives_it()
    {
        await fixture.ResetUnitAsync();
        var id = await NewProjectAsync();

        await using var db = fixture.CreateContext();
        (await Service(db).DeleteProjectAsync(id, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Deleted>();

        await using var verify = fixture.CreateContext();
        (await verify.Projects.AnyAsync(p => p.Id == id)).ShouldBeFalse();

        // AuditLogs carries no foreign key to Projects, precisely so that "who deleted it"
        // outlives the thing deleted.
        (await verify.AuditLogs.SingleAsync(a => a.Action == "project.deleted")).EntityId.ShouldBe(id);
    }

    [Fact]
    public async Task A_project_with_apartments_is_not_deleted()
    {
        await fixture.ResetUnitAsync();
        await UnpublishFixtureProjectAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).DeleteProjectAsync(
            fixture.ProjectId, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("1 apartment");

        await using var verify = fixture.CreateContext();
        (await verify.Units.AnyAsync(u => u.Id == fixture.UnitId)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_published_project_is_not_deleted()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).DeleteProjectAsync(
            fixture.ProjectId, Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("Unpublish it");
    }

    [Fact]
    public async Task Prose_is_saved_once_per_language_not_once_per_save()
    {
        await fixture.ResetUnitAsync();
        var id = await NewProjectAsync();

        await using (var first = fixture.CreateContext())
        {
            await Service(first).SaveTranslationAsync(
                id, Cultures.Romanian, Prose("Prima versiune"), Actor, "dev", CancellationToken.None);
        }

        await using (var second = fixture.CreateContext())
        {
            await Service(second).SaveTranslationAsync(
                id, Cultures.Romanian, Prose("A doua versiune"), Actor, "dev", CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var rows = await verify.ProjectTranslations.Where(t => t.ProjectId == id).ToListAsync();

        rows.Count.ShouldBe(1);
        rows[0].Summary.ShouldBe("A doua versiune");

        // Prose is audited against the project, so the audit screen — which filters on a single
        // entity id — shows it in that project's own trail.
        var audit = await verify.AuditLogs.FirstAsync(a => a.Action == "translation.saved");
        audit.EntityId.ShouldBe(id);
        audit.After.ShouldContain(Cultures.Romanian);
    }

    /// <summary>
    /// The reservation notice falls back to Romanian rather than to a blank, and the English
    /// row existing does not count as an answer.
    /// </summary>
    /// <remarks>
    /// The English row is written here deliberately. Saving any English prose at all creates
    /// one, and a plain <c>ForCulture</c> would then match it, find the notice empty, and show
    /// an English reader nothing at all while the Romanian notice sat right beside it.
    /// </remarks>
    [Fact]
    public async Task A_reservation_notice_written_only_in_Romanian_still_reaches_an_English_reader()
    {
        await fixture.ResetUnitAsync();
        var id = await NewProjectAsync();

        await using (var db = fixture.CreateContext())
        {
            await Service(db).SaveTranslationAsync(
                id,
                Cultures.Romanian,
                new TranslationEdit(null, null, null, null, null, "Adu buletinul la semnare."),
                Actor,
                "dev",
                CancellationToken.None);

            await Service(db).SaveTranslationAsync(
                id,
                Cultures.English,
                new TranslationEdit(null, "An English summary, and no notice.", null, null, null, null),
                Actor,
                "dev",
                CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();
        var rows = await verify.ProjectTranslations.Where(t => t.ProjectId == id).ToListAsync();

        rows.ReservationNoticeFor(Cultures.English).ShouldBe("Adu buletinul la semnare.");
        rows.ReservationNoticeFor(Cultures.Romanian).ShouldBe("Adu buletinul la semnare.");

        // Nothing written anywhere stays nothing, rather than becoming a blank paragraph.
        rows.Where(t => t.Culture == "none").ReservationNoticeFor(Cultures.English).ShouldBeNull();
    }

    [Fact]
    public async Task A_developer_in_use_is_not_removed()
    {
        await fixture.ResetUnitAsync();

        Guid developerId;

        await using (var db = fixture.CreateContext())
        {
            var created = await Service(db).CreateDeveloperAsync(
                Developer(), Actor, "dev", CancellationToken.None);

            developerId = created.ShouldBeOfType<AdminWriteResult.Created>().Id;

            await db.Projects
                .Where(p => p.Id == fixture.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeveloperId, developerId));
        }

        await using var attempt = fixture.CreateContext();
        var result = await Service(attempt).DeleteDeveloperAsync(
            developerId, Actor, "dev", CancellationToken.None);

        // The foreign key is SET NULL, so without this guard the database would quietly strip
        // the developer off a live project rather than refuse.
        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("1 project");
    }

    [Fact]
    public async Task Two_developers_cannot_share_a_name()
    {
        await fixture.ResetUnitAsync();

        await using (var db = fixture.CreateContext())
        {
            await Service(db).CreateDeveloperAsync(Developer(), Actor, "dev", CancellationToken.None);
        }

        await using var again = fixture.CreateContext();
        var result = await Service(again).CreateDeveloperAsync(
            Developer(), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("Vertical Development");
    }

    [Fact]
    public async Task An_unused_developer_is_removed()
    {
        await fixture.ResetUnitAsync();

        Guid id;

        await using (var db = fixture.CreateContext())
        {
            var created = await Service(db).CreateDeveloperAsync(
                Developer(), Actor, "dev", CancellationToken.None);

            id = created.ShouldBeOfType<AdminWriteResult.Created>().Id;
        }

        await using var remove = fixture.CreateContext();
        (await Service(remove).DeleteDeveloperAsync(id, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Deleted>();

        await using var verify = fixture.CreateContext();
        (await verify.Developers.AnyAsync(d => d.Id == id)).ShouldBeFalse();
    }
}
