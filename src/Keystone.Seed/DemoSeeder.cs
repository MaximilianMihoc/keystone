using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Domain.Pricing;
using Keystone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Seed;

/// <summary>
/// Loads one fictional development, so a fresh database has something to show.
/// </summary>
/// <remarks>
/// Everything here is invented: the developer, the project and every price. The units are
/// generated rather than read from a file, so the seed needs nothing outside this repository.
/// </remarks>
public static class DemoSeeder
{
    public const string Slug = "gradina-teilor";

    /// <summary>The four apartments on every floor: type, usable area, balcony, facing.</summary>
    private static readonly (string Type, decimal Area, decimal Balcony, string Facing)[] Layout =
    [
        ("Studio",  38.40m,  5.60m, "N"),
        ("2 camere", 55.90m, 7.80m, "E"),
        ("2 camere", 61.20m, 8.10m, "S"),
        ("3 camere", 82.70m, 11.90m, "W")
    ];

    private static readonly Dictionary<string, string> EnglishUnitTypeNames = new()
    {
        ["Studio"] = "Studio",
        ["2 camere"] = "2 rooms",
        ["3 camere"] = "3 rooms"
    };

    private const int Floors = 8;

    public static async Task<int> SeedAsync(KeystoneDbContext db, CancellationToken ct)
    {
        if (await db.Projects.AnyAsync(p => p.Slug == Slug, ct))
        {
            Console.WriteLine($"  '{Slug}' already seeded; skipping.");
            return 0;
        }

        var project = new Project
        {
            Slug = Slug,
            Name = "Grădina Teilor",
            City = "Cluj-Napoca",
            Developer = new Developer
            {
                Name = "Northfield Development",
                Description = "A fictional developer, invented for this demo.",
                YearsActive = 12,
                UnitsBuilt = 640
            },
            Status = ProjectStatus.Selling,
            UnitsCommitted = Floors * Layout.Length,
            DeliveryDate = new DateOnly(2028, 6, 30),
            DeliveryGraceMonths = 6,
            RentalProfile = RentalProfile.Mixed,
            ReservationFee = 2_000m,
            Currency = "EUR",
            HoldDurationHours = 48,
            MaxActiveHoldsPerProspect = 3,
            MaxParkingSpacesPerReservation = 0,
            PublishedAt = DateTimeOffset.UtcNow
        };

        project.Translations.Add(new ProjectTranslation
        {
            Culture = Cultures.Romanian,
            City = "Cluj-Napoca",
            Summary =
                "Un imobil rezidențial fictiv P+8E lângă un parc de tei, cu 32 de apartamente, " +
                "standard nZEB și spații comerciale la parter.",
            PenaltyClause =
                "Dacă dezvoltatorul nu livrează la termen + 6 luni, din culpa lui, clientul poate " +
                "retrage avansul și primește o penalitate de 50% din avansul achitat.",
            Parking = "Parcarea se cumpără direct de la dezvoltator, separat de apartament."
        });

        project.Translations.Add(new ProjectTranslation
        {
            Culture = Cultures.English,
            City = "Cluj-Napoca",
            Summary =
                "A fictional G+8 residential building beside a park of lime trees: 32 apartments, " +
                "built to nZEB standard, with commercial units at ground level.",
            PenaltyClause =
                "If the developer fails to deliver within the agreed date plus six months, " +
                "through its own fault, the buyer may withdraw the deposit and receives a " +
                "penalty of 50% of the deposit paid.",
            Parking = "Parking is bought directly from the developer, separately from the apartment."
        });

        // 15% advance on the developer price, the rest at delivery. Romania's rates since
        // August 2025: 21% standard, 11% reduced.
        project.PricingRules.Add(new PricingRule
        {
            AdvancePct = 0.1500m,
            CesiuneVatPct = 0.2100m,
            VatStandard = 0.2100m,
            VatReduced = 0.1100m,
            Tranches =
            [
                new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
                new TrancheDefinition("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
            ]
        });

        // The catalogue is portal-wide, so a type another project already introduced is reused.
        var unitTypes = await db.UnitTypes.ToDictionaryAsync(t => t.Name, ct);

        UnitType TypeFor(string name)
        {
            if (unitTypes.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var created = new UnitType { Name = name, SortOrder = unitTypes.Count };
            created.Translations.Add(new UnitTypeTranslation
            {
                Culture = Cultures.English,
                DisplayName = EnglishUnitTypeNames[name]
            });

            unitTypes[name] = created;
            db.UnitTypes.Add(created);
            return created;
        }

        for (var floor = 1; floor <= Floors; floor++)
        {
            for (var position = 0; position < Layout.Length; position++)
            {
                var (type, area, balcony, facing) = Layout[position];

                // Higher floors cost more per square metre; the assignment fee is a slice of it.
                var price = Math.Round((area + balcony / 2) * (1_850m + 45m * floor), 2);
                var cesiune = Math.Round(price * 0.08m, 2);

                project.Units.Add(new Unit
                {
                    Crt = (floor - 1) * Layout.Length + position + 1,
                    Code = $"Ap.{floor}{position + 1:00}",
                    Building = "A",
                    FloorLevel = floor,
                    UnitType = TypeFor(type),
                    Orientation = facing,
                    AreaBuilt = Math.Round(area * 1.18m, 2),
                    AreaUsable = area,
                    AreaBalcony = balcony,
                    BasePriceNet = price,
                    CesiunePriceNet = cesiune,

                    // The reduced rate is for homes up to 120 m² and roughly 120,000 EUR.
                    ReducedVatEligible = price <= 120_000m,

                    // A few already sold, so the inventory does not look untouched.
                    State = (floor * 7 + position * 3) % 5 == 0 ? UnitState.Reserved : UnitState.Available
                });
            }
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return project.Units.Count;
    }
}
