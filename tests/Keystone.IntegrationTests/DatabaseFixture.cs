using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Domain.Pricing;
using Keystone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Keystone.IntegrationTests;

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
    /// <summary>
    /// How to reach a SQL Server, without saying which one.
    /// </summary>
    /// <remarks>
    /// Defaults to the local Express instance so the tests run on this machine with no setup.
    /// CI sets KEYSTONE_TEST_SQL to point at a container with a password, because a build agent
    /// has no Express install and no Windows authentication to offer it.
    /// </remarks>
    private static string Template =>
        Environment.GetEnvironmentVariable("KEYSTONE_TEST_SQL")
        ?? @"Server=.\SQLEXPRESS;Database={0};Trusted_Connection=True;TrustServerCertificate=True";

    private static string For(string database) => string.Format(Template, database);

    private static string Master => For("master");

    public string DatabaseName { get; } = $"KeystoneTests_{Guid.NewGuid():N}";

    public string ConnectionString => For(DatabaseName);

    public Guid ProjectId { get; private set; }

    public Guid UnitId { get; private set; }

    /// <summary>The values the fixture's unit is restored to between tests.</summary>
    public static (decimal Price, decimal Cesiune, decimal AreaUsable) SeedPrice
        => (135_776m, 14_444.62m, 45.71m);

    /// <summary>
    /// The values the fixture's project is created with, and restored to between tests.
    /// </summary>
    /// <remarks>
    /// Read by both the seeder and the reset, so the two cannot drift apart — which they would,
    /// silently, the first time somebody changed one of them.
    /// </remarks>
    public static (string Slug, string Name, string City, ProjectStatus Status, DateTimeOffset PublishedAt) SeedProject
        => ("test-project", "Test Project", "București", ProjectStatus.Selling, PublishedAt);

    private static readonly DateTimeOffset PublishedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// The payment plan the fixture's project is seeded with, and restored to between tests.
    /// </summary>
    /// <remarks>
    /// A fresh instance every call: the tranche list is a JSON-mapped reference type, so handing
    /// the same one to two contexts would let a test's edit reach into the next test's seed.
    /// </remarks>
    public static PricingRule SeededRule() => new()
    {
        AdvancePct = 0.15m,
        CesiuneVatPct = 0.19m,
        VatStandard = 0.19m,
        Tranches =
        [
            new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
            new TrancheDefinition("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
        ]
    };

    /// <summary>
    /// A context configured the way production is.
    /// </summary>
    /// <remarks>
    /// <c>EnableRetryOnFailure</c> matters here and is not incidental: it changes the execution
    /// strategy, and a strategy that retries refuses user-initiated transactions unless the
    /// whole transaction is wrapped as one replayable unit. A fixture without it passes tests
    /// that then fail on the first real request, which is exactly what happened.
    /// </remarks>
    public KeystoneDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<KeystoneDbContext>()
            .UseSqlServer(ConnectionString, sql =>
                sql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(5), null))
            .Options);

    /// <summary>Plain context for fixture setup, where migrations need no retry strategy.</summary>
    private KeystoneDbContext CreatePlainContext() =>
        new(new DbContextOptionsBuilder<KeystoneDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);

    public async Task InitializeAsync()
    {
        await using (var master = new KeystoneDbContext(
            new DbContextOptionsBuilder<KeystoneDbContext>().UseSqlServer(Master).Options))
        {
            await master.Database.ExecuteSqlRawAsync($"CREATE DATABASE [{DatabaseName}]");
        }

        await using var db = CreatePlainContext();
        await db.Database.MigrateAsync();
        await SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var master = new KeystoneDbContext(
            new DbContextOptionsBuilder<KeystoneDbContext>().UseSqlServer(Master).Options);

        await master.Database.ExecuteSqlRawAsync(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{DatabaseName}]");
    }

    /// <summary>Puts the single unit under test back to Available between tests.</summary>
    public async Task ResetUnitAsync()
    {
        await using var db = CreateContext();

        // Parking first of all: a space points at a hold and a reservation with NoAction, so
        // neither can be deleted while one still references it.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [ParkingSpaces]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [ParkingPrices]");

        // Reservations next: they hold restricting foreign keys to both units and prospects,
        // so deleting either out from under them fails.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Reservations]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [HoldRequests]");
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

        // Prose the fixture's own project acquired. The seeder writes none, so all of it is
        // something a test put there.
        await db.ProjectTranslations.Where(t => t.ProjectId == ProjectId).ExecuteDeleteAsync();

        // The payment plan is writable now, and a test that corrects a rate or schedules a change
        // would otherwise leave it there — quietly repricing every later test that reads a
        // tranche. Cheaper to put the seeded plan back than to restore it field by field.
        await db.PricingRules.Where(r => r.ProjectId == ProjectId).ExecuteDeleteAsync();

        var rule = SeededRule();
        rule.ProjectId = ProjectId;
        db.PricingRules.Add(rule);
        await db.SaveChangesAsync();

        // Developers are global rather than per-project, so the sweep above never reaches them
        // — and Developers.Name is unique, so the second test to name one the same thing fails
        // on a constraint that has nothing to do with what it was testing. The foreign key is
        // ON DELETE SET NULL, so this also detaches the fixture project.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Developers]");

        // The project is writable now, in every column its setup screen owns. PublishedAt is
        // the one that bites: a test that unpublishes and does not put it back leaves every
        // later public-page assertion quietly testing nothing.
        var seededProject = SeedProject;

        await db.Projects
            .Where(p => p.Id == ProjectId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.MaxParkingSpacesPerReservation, 0)
                .SetProperty(p => p.Slug, seededProject.Slug)
                .SetProperty(p => p.Name, seededProject.Name)
                .SetProperty(p => p.City, seededProject.City)
                .SetProperty(p => p.Status, seededProject.Status)
                .SetProperty(p => p.SortOrder, 0)
                .SetProperty(p => p.PublishedAt, (DateTimeOffset?)seededProject.PublishedAt)
                .SetProperty(p => p.DeveloperId, (Guid?)null)
                .SetProperty(p => p.UnitsCommitted, (int?)1)
                .SetProperty(p => p.DeliveryDate, (DateOnly?)null)
                .SetProperty(p => p.DeliveryGraceMonths, 0)
                .SetProperty(p => p.RentalProfile, (RentalProfile?)null)
                .SetProperty(p => p.ReservationFee, (decimal?)4_000m)
                .SetProperty(p => p.Currency, "EUR")
                .SetProperty(p => p.HoldDurationHours, 48)
                .SetProperty(p => p.MaxActiveHoldsPerProspect, 3)
                .SetProperty(p => p.MapsUrl, (string?)null));

        // Units other tests created — the importer makes them by the sheetful.
        await db.Units.Where(u => u.ProjectId == ProjectId && u.Id != UnitId).ExecuteDeleteAsync();
        await db.UnitTypes.Where(t => !t.Units.Any()).ExecuteDeleteAsync();

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
                .SetProperty(u => u.AdminNotes, (string?)null));
    }

    /// <summary>
    /// Adds <paramref name="count"/> extra apartments and returns their ids, so a test can race
    /// buyers of different flats for the same parking space. ResetUnitAsync deletes them again.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AddUnitsAsync(int count)
    {
        await using var db = CreateContext();

        var typeId = await db.UnitTypes.Select(t => t.Id).FirstAsync();

        var units = Enumerable.Range(0, count).Select(i => new Unit
        {
            ProjectId = ProjectId,
            Code = $"Ap.9{i:D2}",
            UnitTypeId = typeId,
            FloorLevel = 9,
            AreaUsable = 45m,
            AreaBalcony = 4m,
            BasePriceNet = 100_000m,
            CesiunePriceNet = 10_000m,
            State = UnitState.Available
        }).ToList();

        db.Units.AddRange(units);
        await db.SaveChangesAsync();

        return [.. units.Select(u => u.Id)];
    }

    /// <summary>
    /// Gives the fixture's project a car park: one price for the kind, and <paramref name="spaces"/>
    /// numbered spaces of it. Call after <see cref="ResetUnitAsync"/>, which clears both tables.
    /// </summary>
    public async Task SeedParkingAsync(
        int spaces,
        ParkingSpaceKind kind = ParkingSpaceKind.Underground,
        int maxPerReservation = 2,
        decimal priceNet = 8_000m,
        decimal depositNet = 2_176m)
    {
        await using var db = CreateContext();

        await db.Projects
            .Where(p => p.Id == ProjectId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.MaxParkingSpacesPerReservation, maxPerReservation));

        db.ParkingPrices.Add(new ParkingPrice
        {
            ProjectId = ProjectId,
            Kind = kind,
            PriceNet = priceNet,
            DepositNet = depositNet
        });

        for (var i = 1; i <= spaces; i++)
        {
            db.ParkingSpaces.Add(new ParkingSpace
            {
                ProjectId = ProjectId,
                Code = $"A{i}",
                Kind = kind,
                Level = kind is ParkingSpaceKind.Underground ? -1 : 0
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedAsync(KeystoneDbContext db)
    {
        var seeded = SeedProject;

        var project = new Project
        {
            Slug = seeded.Slug,
            Name = seeded.Name,
            City = seeded.City,
            Status = seeded.Status,
            UnitsCommitted = 1,
            HoldDurationHours = 48,
            ReservationFee = 4_000m,
            PublishedAt = seeded.PublishedAt
        };

        project.PricingRules.Add(SeededRule());

        var type = new UnitType { Name = "2 camere", SortOrder = 0 };
        db.UnitTypes.Add(type);

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
