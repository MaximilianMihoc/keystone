using Keystone.Domain;
using Keystone.Infrastructure;
using Keystone.Seed;
using Microsoft.EntityFrameworkCore;

// Loads staff accounts and one fictional development into the local database, so
// development starts from something to look at rather than an empty schema.
//
//   dotnet run --project src/Keystone.Seed
//   dotnet run --project src/Keystone.Seed -- --reset

var reset = args.Contains("--reset", StringComparer.OrdinalIgnoreCase);

await using var db = new KeystoneDbContextFactory().CreateDbContext([]);

Console.WriteLine($"Database: {db.Database.GetConnectionString()}");

if (reset)
{
    Console.WriteLine("Resetting seeded data...");
    await db.Database.ExecuteSqlRawAsync(
        """
        DELETE FROM [UnitAssets];
        DELETE FROM [ParkingSpaces];
        DELETE FROM [ParkingPrices];
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

Console.WriteLine("Seeding staff...");
var staffAdded = await StaffSeeder.SeedAsync(db, CancellationToken.None);
Console.WriteLine($"  {staffAdded} staff account(s) added.");

Console.WriteLine("Seeding the demo project...");
var seeded = await DemoSeeder.SeedAsync(db, CancellationToken.None);

Console.WriteLine();
Console.WriteLine($"Seeded {seeded} units.");
Console.WriteLine();

// Report what landed, with a sample of the pricing engine's arithmetic.
var project = await db.Projects
    .Include(p => p.Units).ThenInclude(u => u.UnitType)
    .Include(p => p.PricingRules)
    .AsSplitQuery()
    .FirstOrDefaultAsync(p => p.Slug == DemoSeeder.Slug);

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
    var breakdown = Keystone.Domain.Pricing.TrancheCalculator.Calculate(
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
