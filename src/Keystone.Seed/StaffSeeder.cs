using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Seed;

/// <summary>
/// Creates the staff accounts that can sign in to the admin.
/// </summary>
/// <remarks>
/// <para>
/// The people are fictional and the addresses are <c>.invalid</c> placeholders, so a seeded
/// database can never send a sign-in link to anybody real.
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
        ("ana@keystone.invalid",       "Ana Popescu",         StaffRole.Admin),
        ("mihai@keystone.invalid",     "Mihai Ionescu",       StaffRole.Admin),
        ("elena@keystone.invalid",     "Elena Radu",          StaffRole.Sales),
        ("andrei@keystone.invalid",    "Andrei Stan",         StaffRole.Sales),

        // The developer's own account, so the admin can actually be signed into during
        // development. Configured to receive mail via Email:RedirectAllTo.
        ("dev@keystone.invalid",       "Developer",           StaffRole.Admin)
    ];

    public static async Task<int> SeedAsync(KeystoneDbContext db, CancellationToken ct)
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
