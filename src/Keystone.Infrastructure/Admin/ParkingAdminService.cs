using System.Globalization;
using System.Text.Json;
using Keystone.Domain;
using Keystone.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keystone.Infrastructure.Admin;

/// <summary>A range of bays, the way a car park is actually described.</summary>
public sealed record SpaceRange(string Prefix, int From, int To);

/// <summary>What may be changed about one bay. Null means not sent.</summary>
public sealed record SpaceEdit(string? Code, ParkingSpaceKind? Kind, int? Level, string? AdminNotes);

/// <summary>How many bays a range wrote, and which it left alone because they were taken.</summary>
public sealed record SpacesAdded(int Created, IReadOnlyList<string> Skipped);

/// <summary>How many bays a range removed, and which it kept because somebody was using them.</summary>
public sealed record SpacesRemoved(int Removed, IReadOnlyList<string> Kept);

/// <summary>
/// The car park, for staff.
/// </summary>
/// <remarks>
/// <para>
/// Spaces are entered rather than imported: the operator's sheets price parking per apartment and never
/// enumerate the bays, so there is nothing to import from yet.
/// </para>
/// <para>
/// This exists because these writes used to happen in the endpoints, directly against the
/// context, with no audit row — the only such paths in the codebase. A bay carries a state and
/// gets sold, and its price is what a reservation snapshots at the moment of sale, so "who
/// deleted A-23" and "who changed the underground price" are questions that get asked.
/// </para>
/// <para>
/// Every operation is audited against the <em>project</em> rather than the space, including the
/// single-bay ones, with the code in the payload. The audit screen filters on one entity id, and
/// a deletion recorded against the bay's own id would be unfindable the moment the bay was gone.
/// </para>
/// </remarks>
public sealed class ParkingAdminService(
    KeystoneDbContext db,
    TimeProvider clock,
    ILogger<ParkingAdminService> logger)
{
    /// <summary>A whole car park at once is fine; a typo asking for a hundred thousand bays is not.</summary>
    public const int MostAtOnce = 500;

    public async Task<AdminWriteResult> AddSpacesAsync(
        Guid projectId,
        SpaceRange range,
        ParkingSpaceKind kind,
        int level,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            return new AdminWriteResult.NotFound();
        }

        var existing = await db.ParkingSpaces
            .Where(s => s.ProjectId == projectId)
            .Select(s => s.Code)
            .ToListAsync(ct);

        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        var added = new List<string>();

        // Padded to the width of the largest number in the range, so A1–A10 becomes A01–A10.
        // Codes sort as text everywhere — the grid, the allocation order, the email naming the
        // buyer's spaces — and unpadded numbering puts A10 before A2. Car park signage numbers
        // this way for the same reason.
        var width = range.To.ToString(CultureInfo.InvariantCulture).Length;
        var now = clock.GetUtcNow();

        for (var i = range.From; i <= range.To; i++)
        {
            var code = range.Prefix.Trim() + i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

            // Codes already in use are reported rather than failing the whole range: extending a
            // car park from A20 to A40 should not be refused because A20 exists.
            if (!taken.Add(code))
            {
                skipped.Add(code);
                continue;
            }

            db.ParkingSpaces.Add(new ParkingSpace
            {
                ProjectId = projectId,
                Code = code,
                Kind = kind,
                Level = level,
                StateChangedAt = now
            });

            added.Add(code);
        }

        if (added.Count == 0)
        {
            return new AdminWriteResult.Rejected(
                skipped.Count == 0
                    ? "That range holds no spaces."
                    : $"Every space in that range already exists: {Join(skipped)}.");
        }

        // One row for the range rather than one per bay. Adding A01–A40 is a single act, and
        // forty rows would bury the next thing somebody actually needs to find.
        Audit(actorId, actorLabel, projectId, "parking.spaces.added", null, new
        {
            Kind = kind.ToString(),
            Level = level,
            Created = added.Count,
            Codes = added,
            Skipped = skipped
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Actor} added {Count} parking spaces to project {ProjectId}",
            actorLabel, added.Count, projectId);

        return new AdminWriteResult.Payload(new SpacesAdded(added.Count, skipped));
    }

    public async Task<AdminWriteResult> EditSpaceAsync(
        Guid spaceId,
        SpaceEdit edit,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var space = await Describing(spaceId, ct);

        if (space is null)
        {
            return new AdminWriteResult.NotFound();
        }

        // A space someone is holding or has bought is not free to be renumbered, moved to another
        // floor, or reclassified underneath them. The kind matters most: the price a buyer is
        // charged is looked up from it at conversion, so changing it changes what they pay for the
        // bay they were quoted. Only the note is free.
        var describes = edit.Code is not null || edit.Kind is not null || edit.Level is not null;

        if (space.State is not UnitState.Available && describes)
        {
            return new AdminWriteResult.Rejected(InUse(space, "changed"));
        }

        var before = new Dictionary<string, object?>();
        var changes = new Dictionary<string, object?>();

        if (edit.Kind is { } kind && kind != space.Kind)
        {
            before["Kind"] = space.Kind.ToString();
            changes["Kind"] = kind.ToString();
            space.Kind = kind;
        }

        if (edit.Code is { Length: > 0 } code && code.Trim() != space.Code)
        {
            before["Code"] = space.Code;
            changes["Code"] = code.Trim();
            space.Code = code.Trim();
        }

        if (edit.Level is { } level && level != space.Level)
        {
            before["Level"] = space.Level;
            changes["Level"] = level;
            space.Level = level;
        }

        if (edit.AdminNotes is not null && edit.AdminNotes != space.AdminNotes)
        {
            before["AdminNotes"] = space.AdminNotes;
            changes["AdminNotes"] = edit.AdminNotes;
            space.AdminNotes = edit.AdminNotes;
        }

        if (changes.Count == 0)
        {
            return new AdminWriteResult.NoChange();
        }

        var wanted = space.Code;

        Audit(actorId, actorLabel, space.ProjectId, "parking.space.edited",
            before, new { Code = wanted, Fields = changes });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            // UX_ParkingSpaces_ProjectCode. Re-asked rather than assumed: the previous version of
            // this reported every write failure as a duplicate code, so a disk full or a check
            // constraint sent somebody looking at the wrong thing entirely.
            if (!await db.ParkingSpaces.AnyAsync(
                    s => s.ProjectId == space.ProjectId && s.Code == wanted && s.Id != spaceId, ct))
            {
                throw;
            }

            return new AdminWriteResult.Rejected($"Another space here is already called {wanted}.");
        }

        return new AdminWriteResult.Updated(changes);
    }

    public async Task<AdminWriteResult> DeleteSpaceAsync(
        Guid spaceId,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var space = await Describing(spaceId, ct);

        if (space is null)
        {
            return new AdminWriteResult.NotFound();
        }

        if (space.State is not UnitState.Available)
        {
            return new AdminWriteResult.Rejected(InUse(space, "removed"));
        }

        db.ParkingSpaces.Remove(space);

        Audit(actorId, actorLabel, space.ProjectId, "parking.space.removed",
            new { space.Code, Kind = space.Kind.ToString(), space.Level }, null);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Actor} removed parking space {Code}", actorLabel, space.Code);

        return new AdminWriteResult.Deleted();
    }

    public async Task<AdminWriteResult> RemoveRangeAsync(
        Guid projectId,
        SpaceRange range,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        // Matched by code rather than by recomputing the padding: a range may have been added at
        // one width and extended at another, and the codes are what exist.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = range.From; i <= range.To; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            wanted.Add(range.Prefix.Trim() + n);
            wanted.Add(range.Prefix.Trim() + n.PadLeft(2, '0'));
            wanted.Add(range.Prefix.Trim() + n.PadLeft(3, '0'));
        }

        var spaces = await db.ParkingSpaces
            .Include(s => s.Hold!).ThenInclude(h => h.Unit)
            .Include(s => s.Reservation!).ThenInclude(r => r.Unit)
            .Where(s => s.ProjectId == projectId && wanted.Contains(s.Code))
            .ToListAsync(ct);

        // A range is a blunt instrument, so the ones somebody is using are kept and named rather
        // than the whole request being refused because of one.
        var kept = spaces.Where(s => s.State is not UnitState.Available)
            .Select(s => s.Code).Order(StringComparer.Ordinal).ToList();

        var removable = spaces.Where(s => s.State is UnitState.Available).ToList();

        if (removable.Count > 0)
        {
            db.ParkingSpaces.RemoveRange(removable);

            Audit(actorId, actorLabel, projectId, "parking.spaces.removed", new
            {
                Removed = removable.Count,
                Codes = removable.Select(s => s.Code).Order(StringComparer.Ordinal).ToList(),
                Kept = kept
            }, null);

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "{Actor} removed {Count} parking spaces from project {ProjectId}",
                actorLabel, removable.Count, projectId);
        }

        return new AdminWriteResult.Payload(new SpacesRemoved(removable.Count, kept));
    }

    public async Task<AdminWriteResult> SetPriceAsync(
        Guid projectId,
        ParkingSpaceKind kind,
        decimal priceNet,
        decimal depositNet,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        // Checked here rather than left to the foreign key, which would surface as a 500 for what
        // is simply an unknown project.
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            return new AdminWriteResult.NotFound();
        }

        var price = await db.ParkingPrices
            .FirstOrDefaultAsync(p => p.ProjectId == projectId && p.Kind == kind, ct);

        object? before = price is null ? null : new { price.PriceNet, price.DepositNet };

        if (price is null)
        {
            price = new ParkingPrice { ProjectId = projectId, Kind = kind };
            db.ParkingPrices.Add(price);
        }
        else if (price.PriceNet == priceNet && price.DepositNet == depositNet)
        {
            return new AdminWriteResult.NoChange();
        }

        // Reservations already agreed keep their own snapshot, so repricing here never moves what
        // somebody has signed for. The audit row is how the change is explained afterwards.
        price.PriceNet = priceNet;
        price.DepositNet = depositNet;

        Audit(actorId, actorLabel, projectId, "parking.price.set", before,
            new { Kind = kind.ToString(), PriceNet = priceNet, DepositNet = depositNet });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{Actor} set the {Kind} parking price on project {ProjectId}", actorLabel, kind, projectId);

        return new AdminWriteResult.Updated(new Dictionary<string, object?>
        {
            ["PriceNet"] = priceNet,
            ["DepositNet"] = depositNet
        });
    }

    /// <summary>
    /// How many bays one reservation may take.
    /// </summary>
    /// <remarks>
    /// Worth auditing despite being one small integer: setting it to zero silently stops anybody
    /// adding parking to a sale, and the screen gives no hint that somebody chose that.
    /// </remarks>
    public async Task<AdminWriteResult> SetMaxPerReservationAsync(
        Guid projectId,
        int max,
        Guid actorId,
        string actorLabel,
        CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return new AdminWriteResult.NotFound();
        }

        if (project.MaxParkingSpacesPerReservation == max)
        {
            return new AdminWriteResult.NoChange();
        }

        Audit(actorId, actorLabel, projectId, "parking.max.set",
            new { Max = project.MaxParkingSpacesPerReservation }, new { Max = max });

        project.MaxParkingSpacesPerReservation = max;
        await db.SaveChangesAsync(ct);

        return new AdminWriteResult.Updated(new Dictionary<string, object?> { ["Max"] = max });
    }

    private Task<ParkingSpace?> Describing(Guid spaceId, CancellationToken ct) =>
        db.ParkingSpaces
            .Include(s => s.Hold!).ThenInclude(h => h.Unit)
            .Include(s => s.Reservation!).ThenInclude(r => r.Unit)
            .FirstOrDefaultAsync(s => s.Id == spaceId, ct);

    /// <summary>
    /// Why a space cannot be touched, naming the apartment it went with. "It is held" leaves an
    /// admin hunting; "it is held with Ap.105" tells them what to release.
    /// </summary>
    private static string InUse(ParkingSpace space, string verb)
    {
        var unit = space.Reservation?.Unit?.Code ?? space.Hold?.Unit?.Code;
        var state = space.State.ToString().ToLowerInvariant();

        return unit is null
            ? $"{space.Code} is {state} and cannot be {verb}."
            : $"{space.Code} is {state} with {unit} and cannot be {verb}. Free it first.";
    }

    private static string Join(List<string> codes) =>
        codes.Count <= 8 ? string.Join(", ", codes) : string.Join(", ", codes.Take(8)) + "…";

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
            EntityType = "project",
            EntityId = projectId,
            Action = action,
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = after is null ? null : JsonSerializer.Serialize(after)
        });
}
