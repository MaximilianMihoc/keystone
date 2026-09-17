using Hubix.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Infrastructure.Sales;

/// <summary>
/// Finding the person behind an email address, or writing them down for the first time.
/// </summary>
/// <remarks>
/// Four places need this — a hold, a reservation entered by staff, a waitlist place and an
/// enquiry — and each of them used to look the address up and insert if it found nothing. Two
/// requests carrying the same new address arrive together, both find nothing, both insert, and
/// one of them dies on <c>IX_Prospects_Email</c>: a public form returning an error because
/// somebody else was filling it in at the same moment.
/// <para>
/// The unique index is the arbiter rather than the problem. The loser reads the winner's row and
/// carries on with it, which is the shape <see cref="ReservationService"/> already uses against
/// <c>UX_Reservations_OneOpenPerUnit</c>. Checking that the row is now there, rather than reading
/// a provider's error number, keeps it a statement about what happened instead of about SQL
/// Server.
/// </para>
/// </remarks>
internal static class ProspectRegistry
{
    /// <summary>
    /// The prospect for <paramref name="email"/>, and whether this call is what created it.
    /// </summary>
    /// <remarks>
    /// The flag decides how a caller may treat the record, so it is not a detail. A record that
    /// already existed belongs to somebody who has not shown up to the form in hand, and only
    /// its blanks may be filled (issue #30). A losing racer is told <c>false</c> on purpose: by
    /// then the row exists and was written by somebody else's request.
    /// </remarks>
    public static async Task<(Prospect Prospect, bool Created)> GetOrCreateAsync(
        HubixDbContext db,
        string email,
        string source,
        string locale,
        CancellationToken ct)
    {
        var existing = await db.Prospects.FirstOrDefaultAsync(p => p.Email == email, ct);

        if (existing is not null)
        {
            return (existing, false);
        }

        var created = new Prospect
        {
            Email = email,
            Locale = locale,
            Source = source
        };

        db.Prospects.Add(created);

        try
        {
            await db.SaveChangesAsync(ct);

            return (created, true);
        }
        catch (DbUpdateException)
        {
            // Detached first: the row was never written, and leaving it tracked as Added means
            // the caller's next save tries to insert it again.
            db.Entry(created).State = EntityState.Detached;

            var winner = await db.Prospects.FirstOrDefaultAsync(p => p.Email == email, ct);

            if (winner is null)
            {
                // Not the collision this exists for. Anything else is a real fault.
                throw;
            }

            return (winner, false);
        }
    }
}
