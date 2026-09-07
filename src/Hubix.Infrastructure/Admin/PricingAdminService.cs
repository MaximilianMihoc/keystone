using System.Text.Json;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hubix.Infrastructure.Admin;

/// <summary>
/// A project's payment plan: the rates, and the payments they are split into.
/// </summary>
public sealed record PricingRuleEdit(
    decimal AdvancePct,
    decimal VatStandard,
    decimal CesiuneVatPct,
    decimal? VatReduced,
    IReadOnlyList<TrancheDefinition> Tranches);

/// <summary>
/// Creating and changing the rule every price a buyer sees is derived from.
/// </summary>
/// <remarks>
/// <para>
/// Pricing rules are versioned, and the code already reads them that way: both
/// <c>ReservationService</c> and <c>InventoryExporter</c> select with
/// <c>Where(r =&gt; r.EffectiveFrom &lt;= now).MaxBy(r =&gt; r.EffectiveFrom)</c>, so a rule dated in the
/// future sits there harmlessly until its day arrives.
/// </para>
/// <para>
/// That is why saving and scheduling are two different operations rather than one. Correcting a
/// VAT rate somebody typed wrong and announcing that prices rise in June are different acts, and
/// quietly turning the first into a new version would leave the wrong figure in the history as
/// though it had once been true.
/// </para>
/// <para>
/// Editing a rule in effect does not disturb anything already sold: a reservation snapshots its
/// own tranches when it is made, and never recomputes them.
/// </para>
/// </remarks>
public sealed class PricingAdminService(
    HubixDbContext db,
    TimeProvider clock,
    ILogger<PricingAdminService> logger)
{
    /// <summary>
    /// Writes the rule in effect today, creating it if the project has never had one.
    /// </summary>
    public async Task<AdminWriteResult> SaveCurrentAsync(
        Guid projectId,
        PricingRuleEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        if (Invalid(edit) is { } problem)
        {
            return problem;
        }

        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            return new AdminWriteResult.NotFound();
        }

        var now = clock.GetUtcNow();

        var rule = await db.PricingRules
            .Where(r => r.ProjectId == projectId && r.EffectiveFrom <= now)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

        var creating = rule is null;

        if (rule is null)
        {
            rule = new PricingRule { ProjectId = projectId, EffectiveFrom = now };
            db.PricingRules.Add(rule);
        }

        var before = Describe(rule);

        rule.AdvancePct = edit.AdvancePct;
        rule.VatStandard = edit.VatStandard;
        rule.CesiuneVatPct = edit.CesiuneVatPct;
        rule.VatReduced = edit.VatReduced;
        rule.Tranches = edit.Tranches;

        Audit(actorId, actorLabel, projectId, creating ? "pricing.created" : "pricing.edited",
            creating ? null : before, Describe(rule));

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Actor} {Action} the pricing rule for project {ProjectId}",
            actorLabel, creating ? "created" : "edited", projectId);

        return creating
            ? new AdminWriteResult.Created(rule.Id)
            : new AdminWriteResult.Updated(Describe(rule));
    }

    /// <summary>
    /// Adds a rule that takes over on a future date, leaving today's alone.
    /// </summary>
    public async Task<AdminWriteResult> ScheduleAsync(
        Guid projectId,
        PricingRuleEdit edit,
        DateTimeOffset effectiveFrom,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        if (Invalid(edit) is { } problem)
        {
            return problem;
        }

        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            return new AdminWriteResult.NotFound();
        }

        var now = clock.GetUtcNow();

        // A rule dated today or earlier would take effect the moment it is saved, which is what
        // the other operation is for. Scheduling means later.
        if (effectiveFrom <= now)
        {
            return new AdminWriteResult.Rejected(
                "A scheduled change has to start in the future. To change prices now, save them instead.");
        }

        if (await db.PricingRules.AnyAsync(
                r => r.ProjectId == projectId && r.EffectiveFrom == effectiveFrom, ct))
        {
            return new AdminWriteResult.Rejected("There is already a change scheduled for that moment.");
        }

        var rule = new PricingRule
        {
            ProjectId = projectId,
            EffectiveFrom = effectiveFrom,
            AdvancePct = edit.AdvancePct,
            VatStandard = edit.VatStandard,
            CesiuneVatPct = edit.CesiuneVatPct,
            VatReduced = edit.VatReduced,
            Tranches = edit.Tranches
        };

        db.PricingRules.Add(rule);
        Audit(actorId, actorLabel, projectId, "pricing.scheduled", null, Describe(rule));

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Actor} scheduled a pricing change for project {ProjectId} from {From}",
            actorLabel, projectId, effectiveFrom);

        return new AdminWriteResult.Created(rule.Id);
    }

    /// <summary>
    /// Cancels a scheduled change that has not started yet.
    /// </summary>
    /// <remarks>
    /// Only a future rule. One already in effect is what the project has been selling on, and
    /// deleting it would silently hand every price back to whatever came before.
    /// </remarks>
    public async Task<AdminWriteResult> DeleteScheduledAsync(
        Guid ruleId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var rule = await db.PricingRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct);

        if (rule is null)
        {
            return new AdminWriteResult.NotFound();
        }

        if (rule.EffectiveFrom <= clock.GetUtcNow())
        {
            return new AdminWriteResult.Rejected(
                "That payment plan is already in effect, so it cannot be cancelled. Save a correction instead.");
        }

        db.PricingRules.Remove(rule);
        Audit(actorId, actorLabel, rule.ProjectId, "pricing.unscheduled", Describe(rule), null);

        await db.SaveChangesAsync(ct);

        return new AdminWriteResult.Deleted();
    }

    private static AdminWriteResult.Rejected? Invalid(PricingRuleEdit edit)
    {
        var problems = PricingRuleValidator.Validate(
            edit.AdvancePct, edit.VatStandard, edit.CesiuneVatPct, edit.VatReduced, edit.Tranches);

        return problems.Count == 0 ? null : new AdminWriteResult.Rejected(string.Join(" ", problems));
    }

    /// <summary>The whole rule, for the audit trail: a rate changing is the point of the row.</summary>
    private static Dictionary<string, object?> Describe(PricingRule rule) => new()
    {
        ["AdvancePct"] = rule.AdvancePct,
        ["VatStandard"] = rule.VatStandard,
        ["CesiuneVatPct"] = rule.CesiuneVatPct,
        ["VatReduced"] = rule.VatReduced,
        ["EffectiveFrom"] = rule.EffectiveFrom,
        ["Tranches"] = rule.Tranches.Select(t => $"{t.Label}:{t.Basis}{(t.Percentage is { } p ? $" {p}" : "")}{(t.IncludesCesiune ? " +cesiune" : "")}").ToList()
    };

    private void Audit(
        Guid actorId,
        string actorLabel,
        Guid projectId,
        string action,
        object? before,
        object? after) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,

            // Against the project, not the rule: the audit screen filters on one entity id, and
            // "what happened to this project" is the question somebody actually asks.
            EntityType = "project",
            EntityId = projectId,
            Action = action,
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = after is null ? null : JsonSerializer.Serialize(after)
        });
}
