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
/// Managing who can sign in.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public class StaffAdminTests(DatabaseFixture fixture)
{
    private static readonly Guid Actor = Guid.CreateVersion7();

    private StaffAdminService Service(KeystoneDbContext db) =>
        new(db, NullLogger<StaffAdminService>.Instance);

    private async Task ClearStaffAsync()
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [StaffLoginTokens]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [StaffUsers]");
    }

    private async Task<Guid> AddAsync(string email, StaffRole role, bool active = true)
    {
        await using var db = fixture.CreateContext();

        var result = await Service(db).CreateAsync(
            email, new StaffEdit($"Person {email}", role, active), Actor, "dev", CancellationToken.None);

        return result.ShouldBeOfType<AdminWriteResult.Created>().Id;
    }

    [Fact]
    public async Task An_account_is_created_and_audited()
    {
        await fixture.ResetUnitAsync();
        await ClearStaffAsync();

        var id = await AddAsync("new@keystone.invalid", StaffRole.Sales);

        await using var verify = fixture.CreateContext();
        var staff = await verify.StaffUsers.SingleAsync(u => u.Id == id);

        staff.Email.ShouldBe("new@keystone.invalid");
        staff.Role.ShouldBe(StaffRole.Sales);
        staff.IsActive.ShouldBeTrue();

        (await verify.AuditLogs.AnyAsync(a => a.Action == "staff.created")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_new_account_can_immediately_ask_for_its_own_link()
    {
        await fixture.ResetUnitAsync();
        await ClearStaffAsync();
        await AddAsync("newcomer@keystone.invalid", StaffRole.Sales);

        await using var db = fixture.CreateContext();
        var auth = new StaffAuthService(db, TimeProvider.System, NullLogger<StaffAuthService>.Instance);

        // This is why no invitation is sent: creating the account is the whole of granting access.
        var link = await auth.IssueLoginTokenAsync("newcomer@keystone.invalid", null, CancellationToken.None);

        link.ShouldNotBeNull();
    }

    [Fact]
    public async Task Two_accounts_cannot_share_an_address()
    {
        await fixture.ResetUnitAsync();
        await ClearStaffAsync();
        await AddAsync("taken@keystone.invalid", StaffRole.Sales);

        await using var db = fixture.CreateContext();
        var result = await Service(db).CreateAsync(
            "taken@keystone.invalid", new StaffEdit("Someone else", StaffRole.Admin, true),
            Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("already has an account");
    }

    [Fact]
    public async Task Deactivating_stops_them_getting_a_link()
    {
        await fixture.ResetUnitAsync();
        await ClearStaffAsync();
        await AddAsync("keeper@keystone.invalid", StaffRole.Admin);
        var leaver = await AddAsync("leaver@keystone.invalid", StaffRole.Sales);

        await using (var db = fixture.CreateContext())
        {
            await Service(db).EditAsync(
                leaver, new StaffEdit("Leaver", StaffRole.Sales, false), Actor, "dev", CancellationToken.None);
        }

        await using var check = fixture.CreateContext();
        var auth = new StaffAuthService(check, TimeProvider.System, NullLogger<StaffAuthService>.Instance);

        (await auth.IssueLoginTokenAsync("leaver@keystone.invalid", null, CancellationToken.None))
            .ShouldBeNull();

        // The row survives, because audit entries and login tokens point at it.
        (await check.StaffUsers.AnyAsync(u => u.Id == leaver)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted()
    {
        await fixture.ResetUnitAsync();
        await ClearStaffAsync();

        var only = await AddAsync("only@keystone.invalid", StaffRole.Admin);
        await AddAsync("sales@keystone.invalid", StaffRole.Sales);

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            only, new StaffEdit("Only", StaffRole.Sales, true), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("last active admin");
    }

    [Fact]
    public async Task The_last_admin_cannot_be_deactivated()
    {
        await fixture.ResetUnitAsync();
        await ClearStaffAsync();

        var only = await AddAsync("only@keystone.invalid", StaffRole.Admin);

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            only, new StaffEdit("Only", StaffRole.Admin, false), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("last active admin");
    }

    [Fact]
    public async Task An_admin_can_be_demoted_while_another_remains()
    {
        await fixture.ResetUnitAsync();
        await ClearStaffAsync();

        var first = await AddAsync("first@keystone.invalid", StaffRole.Admin);
        await AddAsync("second@keystone.invalid", StaffRole.Admin);

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            first, new StaffEdit("First", StaffRole.Sales, true), Actor, "dev", CancellationToken.None);

        result.ShouldBeOfType<AdminWriteResult.Updated>().Changes.Keys.ShouldContain("Role");
    }

    [Fact]
    public async Task An_inactive_admin_does_not_count_as_a_way_back_in()
    {
        await fixture.ResetUnitAsync();
        await ClearStaffAsync();

        var active = await AddAsync("active@keystone.invalid", StaffRole.Admin);
        await AddAsync("dormant@keystone.invalid", StaffRole.Admin, active: false);

        await using var db = fixture.CreateContext();
        var result = await Service(db).EditAsync(
            active, new StaffEdit("Active", StaffRole.Admin, false), Actor, "dev", CancellationToken.None);

        // A deactivated admin cannot obtain a sign-in link, so counting them would leave the
        // door locked with nobody able to open it.
        result.ShouldBeOfType<AdminWriteResult.Rejected>().Reason.ShouldContain("last active admin");
    }
}
