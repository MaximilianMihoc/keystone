using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Infrastructure;
using Keystone.Infrastructure.Admin;
using Keystone.Infrastructure.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Keystone.IntegrationTests;

/// <summary>
/// Whether an open staff session is still allowed to be one.
/// </summary>
/// <remarks>
/// Role and identity are written into the cookie at sign-in, so without a check against the
/// database, deactivating or demoting somebody left their live session fully privileged until
/// the cookie lapsed — "revoke access" that revoked nothing (issue #34). These cover the
/// decision the cookie handler asks for on each check.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public class StaffSessionTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private static StaffAdminService Admin(KeystoneDbContext db) =>
        new(db, NullLogger<StaffAdminService>.Instance);

    private static StaffAuthService Auth(KeystoneDbContext db) =>
        new(db, TimeProvider.System, NullLogger<StaffAuthService>.Instance);

    private async Task<StaffUser> SignedInAsync(StaffRole role = StaffRole.Admin)
    {
        await using var db = fixture.CreateContext();

        var staff = new StaffUser
        {
            Email = $"staff-{Guid.NewGuid():N}@keystone.invalid",
            Name = "Session Holder",
            Role = role,
            IsActive = true
        };

        // Somebody else to be the admin afterwards. StaffAdminService refuses to deactivate or
        // demote the last active one, which is a different rule and not the one under test.
        db.StaffUsers.Add(new StaffUser
        {
            Email = $"keeper-{Guid.NewGuid():N}@keystone.invalid",
            Name = "The Other Admin",
            Role = StaffRole.Admin,
            IsActive = true
        });

        db.StaffUsers.Add(staff);
        await db.SaveChangesAsync();

        return staff;
    }

    [Fact]
    public async Task An_active_account_keeps_its_session()
    {
        var staff = await SignedInAsync();

        await using var db = fixture.CreateContext();

        (await Auth(db).IsSessionStillGoodAsync(
                staff.Id, nameof(StaffRole.Admin), CancellationToken.None))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Deactivating_an_account_ends_its_open_session()
    {
        var staff = await SignedInAsync();

        await using var db = fixture.CreateContext();

        // Another admin turns them off, which until now changed a column and left the person
        // holding an admin cookie for the rest of the day.
        (await Admin(db).EditAsync(
                staff.Id,
                new StaffEdit(staff.Name, StaffRole.Admin, IsActive: false),
                Actor, "boss@keystone.invalid", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Updated>();

        (await Auth(db).IsSessionStillGoodAsync(
                staff.Id, nameof(StaffRole.Admin), CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Demoting_an_account_ends_the_session_it_was_promoted_in()
    {
        var staff = await SignedInAsync();

        await using var db = fixture.CreateContext();

        (await Admin(db).EditAsync(
                staff.Id,
                new StaffEdit(staff.Name, StaffRole.Sales, IsActive: true),
                Actor, "boss@keystone.invalid", CancellationToken.None))
            .ShouldBeOfType<AdminWriteResult.Updated>();

        // The cookie still says Admin. It is the claim that has to stop being believed.
        (await Auth(db).IsSessionStillGoodAsync(
                staff.Id, nameof(StaffRole.Admin), CancellationToken.None))
            .ShouldBeFalse();

        // Signing in again gets the role they actually have.
        (await Auth(db).IsSessionStillGoodAsync(
                staff.Id, nameof(StaffRole.Sales), CancellationToken.None))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task An_account_that_no_longer_exists_has_no_session()
    {
        await using var db = fixture.CreateContext();

        (await Auth(db).IsSessionStillGoodAsync(
                Guid.CreateVersion7(), nameof(StaffRole.Admin), CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task A_promotion_also_ends_the_session_it_happened_in()
    {
        var staff = await SignedInAsync(StaffRole.Sales);

        await using var db = fixture.CreateContext();

        await Admin(db).EditAsync(
            staff.Id,
            new StaffEdit(staff.Name, StaffRole.Admin, IsActive: true),
            Actor, "boss@keystone.invalid", CancellationToken.None);

        // Not a security hole either way, but the cookie and the database disagreeing is
        // exactly the state this check exists to refuse.
        (await Auth(db).IsSessionStillGoodAsync(
                staff.Id, nameof(StaffRole.Sales), CancellationToken.None))
            .ShouldBeFalse();
    }
}
