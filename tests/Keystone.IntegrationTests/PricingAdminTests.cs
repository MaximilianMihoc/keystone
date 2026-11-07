using Keystone.Domain;
using Keystone.Domain.Pricing;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// Editing the rule every price a buyer sees is derived from.
/// </summary>
/// <remarks>
/// The distinction under test throughout is between correcting today's plan and scheduling a new
/// one. Both write a <c>PricingRule</c>; only one of them leaves the old figures behind.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class PricingAdminTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private PricingAdminService Service(KeystoneDbContext db, TimeProvider? clock = null) =>
        new(db, clock ?? TimeProvider.System, NullLogger<PricingAdminService>.Instance);

    private static PricingRuleEdit Plan(decimal advance = 0.15m, decimal vat = 0.19m) =>
        new(advance, vat, 0.19m, 0.09m, [
            new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new TrancheDefinition("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]);

    private async Task<Guid> EmptyProjectAsync()
    {
        await using var db = fixture.CreateContext();

        var result = await new ProjectAdminService(db, TimeProvider.System, NullLogger<ProjectAdminService>.Instance)
            .CreateProjectAsync(
                new ProjectCreate("Pricing Test", "Cluj", null, null), Actor, "dev", CancellationToken.None);

        return result.ShouldBeOfType<AdminWriteResult.Created>().Id;
    }

    [Fact]
    public async Task The_first_save_creates_the_projects_plan()
    {
        await fixture.ResetUnitAsync();
        var id = await EmptyProjectAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).SaveCurrentAsync(id, Plan(), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Created>();

        await using var verify = fixture.CreateContext();
        var rule = await verify.PricingRules.SingleAsync(r => r.ProjectId == id);

        rule.AdvancePct.ShouldBe(0.15m);
        rule.Tranches.Count.ShouldBe(2);
        (await verify.AuditLogs.AnyAsync(a => a.Action == "pricing.created")).ShouldBeTrue();
    }

    [Fact]
    public async Task Saving_again_corrects_the_plan_rather_than_versioning_it()
    {
        await fixture.ResetUnitAsync();
        var id = await EmptyProjectAsync();

        await using (var first = fixture.CreateContext())
        {
            await Service(first).SaveCurrentAsync(id, Plan(), Actor, "dev", CancellationToken.None);
        }

        await using (var second = fixture.CreateContext())
        {
            await Service(second).SaveCurrentAsync(id, Plan(advance: 0.18m), Actor, "dev", CancellationToken.None);
        }

        await using var verify = fixture.CreateContext();

        // One rule, not two. A typo corrected should not leave the wrong figure in the history
        // looking as though it was once the price.
        var rules = await verify.PricingRules.Where(r => r.ProjectId == id).ToListAsync();

        rules.Count.ShouldBe(1);
        rules[0].AdvancePct.ShouldBe(0.18m);
    }

    [Fact]
    public async Task A_bad_plan_is_refused_with_a_reason()
    {
        await fixture.ResetUnitAsync();
        var id = await EmptyProjectAsync();

        await using var db = fixture.CreateContext();

        var result = await Service(db).SaveCurrentAsync(
            id,
            new PricingRuleEdit(0.15m, 0.19m, 0.19m, null, [
                new TrancheDefinition("Only one", TrancheBasis.AdvancePercentage, false, TrancheDue.AtSigning)
            ]),
            Actor, "dev", CancellationToken.None);

        var rejected = result.ShouldBeOfType<AdminWriteResult.Rejected>();
        rejected.Reason.ShouldContain("cesiune");

        await using var verify = fixture.CreateContext();
        (await verify.PricingRules.AnyAsync(r => r.ProjectId == id)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_scheduled_change_leaves_todays_plan_alone()
    {
        await fixture.ResetUnitAsync();
        var id = await EmptyProjectAsync();

        await using (var save = fixture.CreateContext())
        {
            await Service(save).SaveCurrentAsync(id, Plan(), Actor, "dev", CancellationToken.None);
        }

        var june = DateTimeOffset.UtcNow.AddMonths(3);

        await using (var schedule = fixture.CreateContext())
        {
            var result = await Service(schedule).ScheduleAsync(
                id, Plan(advance: 0.20m), june, Actor, "dev", CancellationToken.None);

            result.ShouldBeOfType<AdminWriteResult.Created>();
        }

        await using var verify = fixture.CreateContext();
        var rules = await verify.PricingRules.Where(r => r.ProjectId == id).ToListAsync();

        rules.Count.ShouldBe(2);

        // The one the rest of the application picks today is still the old one — this is exactly
        // the selection ReservationService and InventoryExporter both make.
        var now = DateTimeOffset.UtcNow;
        var inEffect = rules.Where(r => r.EffectiveFrom <= now).MaxBy(r => r.EffectiveFrom);

        inEffect!.AdvancePct.ShouldBe(0.15m);
    }

    [Fact]
    public async Task A_change_cannot_be_scheduled_for_the_past()
    {
        await fixture.ResetUnitAsync();
        var id = await EmptyProjectAsync();

        await using var db = fixture.CreateContext();
        var result = await Service(db).ScheduleAsync(
            id, Plan(), DateTimeOffset.UtcNow.AddDays(-1), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("in the future");
    }

    [Fact]
    public async Task A_scheduled_change_can_be_cancelled()
    {
        await fixture.ResetUnitAsync();
        var id = await EmptyProjectAsync();

        Guid ruleId;

        await using (var schedule = fixture.CreateContext())
        {
            var created = await Service(schedule).ScheduleAsync(
                id, Plan(), DateTimeOffset.UtcNow.AddMonths(1), Actor, "dev", CancellationToken.None);

            ruleId = created.ShouldBeOfType<AdminWriteResult.Created>().Id;
        }

        await using var db = fixture.CreateContext();
        (await Service(db).DeleteScheduledAsync(ruleId, Actor, "dev", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Deleted>();

        await using var verify = fixture.CreateContext();
        (await verify.PricingRules.AnyAsync(r => r.Id == ruleId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_plan_already_in_effect_cannot_be_cancelled()
    {
        await fixture.ResetUnitAsync();
        var id = await EmptyProjectAsync();

        Guid ruleId;

        await using (var save = fixture.CreateContext())
        {
            var created = await Service(save).SaveCurrentAsync(id, Plan(), Actor, "dev", CancellationToken.None);
            ruleId = created.ShouldBeOfType<AdminWriteResult.Created>().Id;
        }

        await using var db = fixture.CreateContext();
        var result = await Service(db).DeleteScheduledAsync(ruleId, Actor, "dev", CancellationToken.None);

        // Deleting it would silently hand every price back to whatever came before.
        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("already in effect");
    }

    [Fact]
    public async Task Correcting_the_fixture_projects_plan_leaves_exactly_one_rule()
    {
        await fixture.ResetUnitAsync();

        // The seeded project already has a plan, so this is the upsert taking the other branch —
        // and it proves ResetUnitAsync puts the seeded plan back, since the next test to read a
        // tranche would otherwise be pricing against 27%.
        await using var db = fixture.CreateContext();
        await Service(db).SaveCurrentAsync(
            fixture.ProjectId, Plan(advance: 0.27m), Actor, "dev", CancellationToken.None);

        await using var verify = fixture.CreateContext();
        var rules = await verify.PricingRules.Where(r => r.ProjectId == fixture.ProjectId).ToListAsync();

        rules.Count.ShouldBe(1);
        rules[0].AdvancePct.ShouldBe(0.27m);
    }

    [Fact]
    public async Task The_seeded_plan_comes_back_between_tests()
    {
        await fixture.ResetUnitAsync();

        await using var db = fixture.CreateContext();
        var rule = await db.PricingRules.SingleAsync(r => r.ProjectId == fixture.ProjectId);

        rule.AdvancePct.ShouldBe(0.15m);
        rule.Tranches.Count.ShouldBe(2);
    }
}
