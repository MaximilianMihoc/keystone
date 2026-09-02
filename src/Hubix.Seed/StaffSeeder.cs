using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hubix.Seed;

/// <summary>
/// Creates the staff accounts that can sign in to the admin.
/// </summary>
/// <remarks>
/// <para>
/// The names and roles come from hubix.ro. The addresses do <em>not</em>: these are
/// <c>.invalid</c> placeholders, because Hubix has not been approached about this system and
/// nobody there should receive a sign-in link for it. Substitute real addresses only once
/// they are actually using it.
/// </para>
/// <para>
/// Sign-in is passwordless, so seeding an account creates no credential and grants nothing on
/// its own — a link still has to be requested and followed.
/// </para>
/// </remarks>
public static class StaffSeeder
{
    private static readonly (string Email, string Name, StaffRole Role)[] Accounts =
    [
        ("bogdan@hubix.invalid",    "Bogdan Dinu",         StaffRole.Admin),
        ("cristian@hubix.invalid",  "Cristian Istrătescu", StaffRole.Admin),
        ("daniel@hubix.invalid",    "Daniel Tudor",        StaffRole.Sales),
        ("georgiana@hubix.invalid", "Georgiana Bărbuță",   StaffRole.Sales),

        // The developer's own account, so the admin can actually be signed into during
        // development. Configured to receive mail via Email:RedirectAllTo.
        ("dev@hubix.invalid",       "Developer",           StaffRole.Admin)
    ];

    public static async Task<int> SeedAsync(HubixDbContext db, CancellationToken ct)
    {
        var existing = await db.StaffUsers
            .Select(u => u.Email)
            .ToListAsync(ct);

        var added = 0;

        foreach (var (email, name, role) in Accounts)
        {
            if (existing.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            db.StaffUsers.Add(new StaffUser { Email = email, Name = name, Role = role });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return added;
    }
}
