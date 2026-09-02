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
        $@"Server=.\SQLEXPRESS;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True";

    public Guid ProjectId { get; private set; }

    public Guid UnitId { get; private set; }

    /// <summary>The values the fixture's unit is restored to between tests.</summary>
    public static (decimal Price, decimal Cesiune, decimal AreaUsable) SeedPrice
        => (135_776m, 14_444.62m, 45.71m);

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

        // Reservations first: they hold restricting foreign keys to both units and prospects,
        // so deleting either out from under them fails.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Reservations]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Holds]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [AuditLogs]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [WaitlistEntries]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Prospects]");

        // Floors before Assets: a floor points at its plan with NoAction, so the asset
        // cannot go first.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [UnitAssets]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Floors]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Assets]");

        // Projects other tests created; the fixture's own must survive.
        await db.Projects.Where(p => p.Id != ProjectId).ExecuteDeleteAsync();

        // Units other tests created — the importer makes them by the sheetful.
        await db.Units.Where(u => u.ProjectId == ProjectId && u.Id != UnitId).ExecuteDeleteAsync();
        await db.UnitTypes.Where(t => t.ProjectId == ProjectId && !t.Units.Any()).ExecuteDeleteAsync();

        var seeded = SeedPrice;

        // Every field a test can write has to be restored, not just the interesting ones.
        // Leaving one out makes the suite order-dependent, and an order-dependent suite tells
        // you nothing on the day it matters.
        await db.Units
            .Where(u => u.Id == UnitId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.State, UnitState.Available)
                .SetProperty(u => u.Crt, (int?)null)
                .SetProperty(u => u.Building, (string?)null)
                .SetProperty(u => u.Stair, (string?)null)
                .SetProperty(u => u.FloorLevel, 1)
                .SetProperty(u => u.Orientation, (string?)null)
                .SetProperty(u => u.AreaBuilt, (decimal?)null)
                .SetProperty(u => u.AreaBalcony, 4.64m)
                .SetProperty(u => u.BasePriceNet, seeded.Price)
                .SetProperty(u => u.CesiunePriceNet, seeded.Cesiune)
                .SetProperty(u => u.AreaUsable, seeded.AreaUsable)
                .SetProperty(u => u.ReducedVatEligible, false)
                .SetProperty(u => u.ParkingAvailable, false)
                .SetProperty(u => u.ParkingPriceNet, (decimal?)null)
                .SetProperty(u => u.ParkingAdvanceNet, (decimal?)null)
                .SetProperty(u => u.AdminNotes, (string?)null));
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
