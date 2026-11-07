using System.Text.Json;
using Keystone.Domain;
using Keystone.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keystone.Infrastructure.Admin;

/// <summary>Who someone is, and what they may do.</summary>
public sealed record StaffEdit(string Name, StaffRole Role, bool IsActive);

/// <summary>
/// The people who can sign in, and what each of them may change.
/// </summary>
/// <remarks>
/// <para>
/// No invitation is sent, and none is needed. Sign-in is passwordless and self-service: an
/// active account can ask for its own link at the sign-in page, so creating the account is the
/// whole of granting access. Emailing a link from here would be a second way to do the same
/// thing, and one that puts a live sign-in token in somebody's inbox unasked.
/// </para>
/// <para>
/// Deactivating rather than deleting, because <c>StaffLoginToken</c> and every audit row point
/// at the person who acted. <c>IssueLoginTokenAsync</c> checks <c>IsActive</c>, so an inactive
/// account cannot obtain a link, and the record of what they did survives them leaving.
/// </para>
/// </remarks>
public sealed class StaffAdminService(
    KeystoneDbContext db,
    ILogger<StaffAdminService> logger)
{
    public async Task<AdminWriteResult> CreateAsync(
        string email,
        StaffEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var address = email.Trim();

        if (address.Length == 0 || !address.Contains('@'))
        {
            return new AdminWriteResult.Rejected("That does not look like an email address.");
        }

        if (edit.Name.Trim().Length == 0)
        {
            return new AdminWriteResult.Rejected("A colleague needs a name.");
        }

        if (await db.StaffUsers.AnyAsync(u => u.Email == address, ct))
        {
            return new AdminWriteResult.Rejected($"{address} already has an account.");
        }

        var staff = new StaffUser
        {
            Email = address,
            Name = edit.Name.Trim(),
            Role = edit.Role,
            IsActive = edit.IsActive
        };

        db.StaffUsers.Add(staff);
        Audit(actorId, actorLabel, staff.Id, "staff.created", null,
            new { staff.Email, staff.Name, Role = staff.Role.ToString() });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            if (!await db.StaffUsers.AnyAsync(u => u.Email == address, ct))
            {
                throw;
            }

            return new AdminWriteResult.Rejected($"{address} already has an account.");
        }

        logger.LogInformation("{Actor} added staff account {Email}", actorLabel, address);

        return new AdminWriteResult.Created(staff.Id);
    }

    /// <summary>
    /// Changes somebody's name, role, or whether they can sign in at all.
    /// </summary>
    /// <remarks>
    /// The one thing refused is removing the last way in. Admin is the only role that can reach
    /// this screen, so demoting or deactivating the final active admin would lock the door from
    /// the inside and leave a SQL update as the only key — which is the exact situation this
    /// whole piece of work exists to end.
    /// </remarks>
    public async Task<AdminWriteResult> EditAsync(
        Guid staffId,
        StaffEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var staff = await db.StaffUsers.FirstOrDefaultAsync(u => u.Id == staffId, ct);

        if (staff is null)
        {
            return new AdminWriteResult.NotFound();
        }

        if (edit.Name.Trim().Length == 0)
        {
            return new AdminWriteResult.Rejected("A colleague needs a name.");
        }

        var losingAdmin = staff.Role == StaffRole.Admin && staff.IsActive
                          && (edit.Role != StaffRole.Admin || !edit.IsActive);

        if (losingAdmin)
        {
            var others = await db.StaffUsers
                .CountAsync(u => u.Id != staffId && u.Role == StaffRole.Admin && u.IsActive, ct);

            if (others == 0)
            {
                return new AdminWriteResult.Rejected(
                    $"{staff.Name} is the last active admin. Give somebody else the admin role first, " +
                    "or there would be no way back in.");
            }
        }

        var diff = new Dictionary<string, object?>();
        var before = new Dictionary<string, object?>();

        void Set<T>(string name, T current, T incoming, Action<T> apply)
        {
            if (EqualityComparer<T>.Default.Equals(current, incoming))
            {
                return;
            }

            before[name] = current;
            diff[name] = incoming;
            apply(incoming);
        }

        Set("Name", staff.Name, edit.Name.Trim(), v => staff.Name = v);
        Set("IsActive", staff.IsActive, edit.IsActive, v => staff.IsActive = v);

        // Recorded by name, not by ordinal. An audit row reading "Role: 1" is no use to the
        // person asking who granted somebody admin, and the numbers shift if the enum ever
        // gains a member.
        if (staff.Role != edit.Role)
        {
            before["Role"] = staff.Role.ToString();
            diff["Role"] = edit.Role.ToString();
            staff.Role = edit.Role;
        }

        if (diff.Count == 0)
        {
            return new AdminWriteResult.NoChange();
        }

        // Signing out somebody who has just lost their access is not this service's job — the
        // cookie is checked against a policy, not against the row — but their next sign-in link
        // request will be refused, and the cookie lasts twelve hours.
        Audit(actorId, actorLabel, staffId, "staff.edited", before, diff);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Actor} changed staff account {Email}", actorLabel, staff.Email);

        return new AdminWriteResult.Updated(diff);
    }

    private void Audit(
        Guid actorId,
        string actorLabel,
        Guid staffId,
        string action,
        object? before,
        object? after) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = "staff",
            ActorId = actorId,
            ActorLabel = actorLabel,
            EntityType = "staff",
            EntityId = staffId,
            Action = action,
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = after is null ? null : JsonSerializer.Serialize(after)
        });
}
