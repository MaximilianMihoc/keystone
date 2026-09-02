using Hubix.Domain;
using Hubix.Infrastructure;
using Hubix.Seed;
using Microsoft.EntityFrameworkCore;

// Loads real Hubix project data into the local database so development starts from
// something true rather than an empty schema.
//
//   dotnet run --project src/Hubix.Seed
//   dotnet run --project src/Hubix.Seed -- --reset

var reset = args.Contains("--reset", StringComparer.OrdinalIgnoreCase);

var repoRoot = FindRepositoryRoot()
    ?? throw new InvalidOperationException("Could not locate the repository root (Hubix.slnx).");

var seedFile = Path.Combine(repoRoot, "seed-data", "real", "roka-park-view-bucuresti.json");

if (!File.Exists(seedFile))
{
    Console.Error.WriteLine($"Seed file not found: {seedFile}");
    Console.Error.WriteLine(
        "Real project data is gitignored. Copy the extracted JSON into seed-data/real/ first.");
    return 1;
}

await using var db = new HubixDbContextFactory().CreateDbContext([]);

Console.WriteLine($"Database: {db.Database.GetConnectionString()}");

if (reset)
{
    Console.WriteLine("Resetting seeded data...");
    await db.Database.ExecuteSqlRawAsync(
        """
        DELETE FROM [UnitAssets];
        DELETE FROM [Holds];
        DELETE FROM [Reservations];
        DELETE FROM [WaitlistEntries];
        DELETE FROM [Enquiries];
        DELETE FROM [Units];
        DELETE FROM [Floors];
        DELETE FROM [UnitTypes];
        DELETE FROM [PricingRules];
        DELETE FROM [Assets];
        DELETE FROM [Projects];
        DELETE FROM [Developers];
        """);
}

Console.WriteLine("Seeding Roka Park View...");
var seeded = await RokaSeeder.SeedAsync(db, seedFile, CancellationToken.None);

Console.WriteLine();
Console.WriteLine($"Seeded {seeded} units.");
Console.WriteLine();

// Report what landed, and prove the pricing engine agrees with the source spreadsheet.
var project = await db.Projects
    .Include(p => p.Units).ThenInclude(u => u.UnitType)
    .Include(p => p.PricingRules)
    .AsSplitQuery()
    .FirstOrDefaultAsync(p => p.Slug == RokaSeeder.Slug);

if (project is null)
{
    return 0;
}

var rule = project.PricingRules.OrderByDescending(r => r.EffectiveFrom).First();

Console.WriteLine($"{project.Name} — {project.City}");
Console.WriteLine($"  committed:  {project.UnitsCommitted} units");
Console.WriteLine($"  delivery:   {project.DeliveryDate:dd.MM.yyyy} (+{project.DeliveryGraceMonths} months grace)");
Console.WriteLine($"  advance:    {rule.AdvancePct:P2}");
Console.WriteLine();

foreach (var group in project.Units.GroupBy(u => u.UnitType!.Name).OrderBy(g => g.Key))
{
    var free = group.Count(u => u.State == UnitState.Available);
    Console.WriteLine($"  {group.Key,-14} {group.Count(),2} units, {free} available");
}

Console.WriteLine();
Console.WriteLine("  Sample tranche calculation:");

foreach (var unit in project.Units.OrderBy(u => u.Crt).Take(3))
{
    var breakdown = Hubix.Domain.Pricing.TrancheCalculator.Calculate(
        rule, unit.BasePriceNet, unit.CesiunePriceNet ?? 0m, unit.ReducedVatEligible);

    Console.WriteLine(
        $"    {unit.Code,-8} {unit.AreaUsable,6:N2} + {unit.AreaBalcony,5:N2} = {unit.AreaTotal,6:N2} mp   " +
        $"{unit.BasePriceNet,10:N2} EUR net");

    foreach (var tranche in breakdown.Tranches)
    {
        Console.WriteLine(
            $"        {tranche.Label,-10} {tranche.Net,10:N2} net + {tranche.Vat,9:N2} VAT = {tranche.Gross,11:N2}");
    }
}

return 0;

static string? FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hubix.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName;
}
