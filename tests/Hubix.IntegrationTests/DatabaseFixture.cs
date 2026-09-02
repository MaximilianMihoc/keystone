using Hubix.Domain;
using Hubix.Domain.Entities;
using Hubix.Domain.Pricing;
using Hubix.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hubix.IntegrationTests;

/// <summary>
/// A throwaway SQL Server database for the integration tests.
/// </summary>
/// <remarks>
/// Runs against the local SQL Server Express instance rather than Testcontainers, because
/// Docker is not installed on this machine. The trade-off is a shared server; the database
/// itself is created and dropped per run, so tests never touch development data.
/// <para>
/// A real database is not optional here. The behaviour under test — two transactions racing
/// for one row — does not exist in the in-memory provider, and testing it there would prove
/// nothing at all.
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string Master =
        @"Server=.\SQLEXPRESS;Database=master;Trusted_Connection=True;TrustServerCertificate=True";

    public string DatabaseName { get; } = $"HubixTests_{Guid.NewGuid():N}";

    public string ConnectionString =>
        $@"Server=.\SQLEXPRESS;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";

    public Guid ProjectId { get; private set; }

    public Guid UnitId { get; private set; }

    /// <summary>
    /// A context configured the way production is.
    /// </summary>
    /// <remarks>
    /// <c>EnableRetryOnFailure</c> matters here and is not incidental: it changes the execution
    /// strategy, and a strategy that retries refuses user-initiated transactions unless the
    /// whole transaction is wrapped as one replayable unit. A fixture without it passes tests
    /// that then fail on the first real request, which is exactly what happened.
    /// </remarks>
    public HubixDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<HubixDbContext>()
            .UseSqlServer(ConnectionString, sql =>
                sql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(5), null))
            .Options);

    /// <summary>Plain context for fixture setup, where migrations need no retry strategy.</summary>
    private HubixDbContext CreatePlainContext() =>
        new(new DbContextOptionsBuilder<HubixDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);

    public async Task InitializeAsync()
    {
        await using (var master = new HubixDbContext(
            new DbContextOptionsBuilder<HubixDbContext>().UseSqlServer(Master).Options))
        {
            await master.Database.ExecuteSqlRawAsync($"CREATE DATABASE [{DatabaseName}]");
        }

        await using var db = CreatePlainContext();
        await db.Database.MigrateAsync();
        await SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var master = new HubixDbContext(
            new DbContextOptionsBuilder<HubixDbContext>().UseSqlServer(Master).Options);

        await master.Database.ExecuteSqlRawAsync(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{DatabaseName}]");
    }

    /// <summary>Puts the single unit under test back to Available between tests.</summary>
    public async Task ResetUnitAsync()
    {
        await using var db = CreateContext();

        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Holds]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [AuditLogs]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [WaitlistEntries]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Prospects]");

        await db.Units
            .Where(u => u.Id == UnitId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.State, UnitState.Available));
    }

    private async Task SeedAsync(HubixDbContext db)
    {
        var project = new Project
        {
            Slug = "test-project",
            Name = "Test Project",
            City = "București",
            Status = ProjectStatus.Selling,
            UnitsCommitted = 1,
            HoldDurationHours = 48,
            ReservationFee = 4_000m,
            PublishedAt = DateTimeOffset.UtcNow
        };

        project.PricingRules.Add(new PricingRule
        {
            AdvancePct = 0.15m,
            CesiuneVatPct = 0.19m,
            VatStandard = 0.19m,
            Tranches =
            [
                new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
                new TrancheDefinition("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
            ]
        });

        var type = new UnitType { Name = "2 camere", SortOrder = 0 };
        project.UnitTypes.Add(type);

        var unit = new Unit
        {
            Code = "Ap.101",
            UnitType = type,
            FloorLevel = 1,
            AreaUsable = 45.71m,
            AreaBalcony = 4.64m,
            BasePriceNet = 135_776m,
            CesiunePriceNet = 14_444.62m,
            State = UnitState.Available
        };

        project.Units.Add(unit);

        db.Projects.Add(project);
        await db.SaveChangesAsync();

        ProjectId = project.Id;
        UnitId = unit.Id;
    }
}

[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
