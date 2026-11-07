using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Domain.Pricing;
using Keystone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Seed;

/// <summary>
/// Loads Roka Park View from its real inventory sheet.
/// </summary>
/// <remarks>
/// <para>
/// Only Roka is seeded. The other four projects have materially different column layouts, and
/// mapping them belongs in the importer's mapping UI (M7) rather than in a hardcoded seeder.
/// </para>
/// <para>
/// Project facts come from the prospectus <c>Apartamente Roka Park View - Bucuresti.docx</c>.
/// </para>
/// </remarks>
public static class RokaSeeder
{
    public const string Slug = "roka-park-view";

    /// <summary>
    /// English display names for the unit types Hubix uses. "Studio Dublu" is a double-aspect
    /// studio rather than two studios, hence the wording.
    /// </summary>
    private static readonly Dictionary<string, string> EnglishUnitTypeNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Studio"] = "Studio",
            ["Studio Dublu"] = "Double studio",
            ["2 camere"] = "2 rooms",
            ["3 camere"] = "3 rooms"
        };

    public static async Task<int> SeedAsync(KeystoneDbContext db, string seedFilePath, CancellationToken ct)
    {
        if (await db.Projects.AnyAsync(p => p.Slug == Slug, ct))
        {
            Console.WriteLine($"  '{Slug}' already seeded; skipping.");
            return 0;
        }

        var sheet = SheetReader.Read(seedFilePath);
        Console.WriteLine($"  Read {sheet.Rows.Count} rows from {sheet.SourceFile}");

        var developer = await db.Developers.FirstOrDefaultAsync(d => d.Name == "Roka Development", ct)
            ?? new Developer
            {
                Name = "Roka Development",
                Description = "Bucharest developer, 20+ years active, 1000+ apartments delivered.",
                YearsActive = 20,
                UnitsBuilt = 1000
            };

        var project = new Project
        {
            Slug = Slug,
            Name = "Roka Park View",
            City = "București",
            Developer = developer,
            Status = ProjectStatus.Selling,

            // The prospectus says 20 in its description and 30 in its contract section.
            // The inventory sheet has 20 rows, so 20 is correct. Stated once, here.
            UnitsCommitted = 20,

            DeliveryDate = new DateOnly(2027, 10, 28),
            DeliveryGraceMonths = 6,
            RentalProfile = RentalProfile.Mixed,
            ReservationFee = 4_000m,
            Currency = "EUR",
            HoldDurationHours = 48,
            MaxActiveHoldsPerProspect = 3,

            // Roka's parking is bought from the developer rather than through Hubix, so no
            // space is offered with an apartment here. The translated note below says so.
            MaxParkingSpacesPerReservation = 0,

            PublishedAt = DateTimeOffset.UtcNow
        };

        // Romanian is Hubix's own wording, taken from the prospectus. English is a
        // translation of it, and is what a visitor sees unless their browser asks for Romanian.
        project.Translations.Add(new ProjectTranslation
        {
            Culture = Cultures.Romanian,
            City = "București",
            Summary =
                "Ansamblu rezidențial premium vizavi de parcul Plumbuita, București. Un imobil " +
                "2S+P+9E, 58 de apartamente, standard nZEB, fațadă ventilată, parter comercial.",
            PenaltyClause =
                "Dacă dezvoltatorul nu livrează la timp + 6 luni, din culpa lui, clientul poate " +
                "retrage avansul și primește o penalitate de 50% din avansul achitat.",
            Parking =
                "Parcarea la acest proiect se cumpără direct de la dezvoltator, separat de " +
                "apartament."
        });

        project.Translations.Add(new ProjectTranslation
        {
            Culture = Cultures.English,
            City = "Bucharest",
            Summary =
                "Premium residential development facing Plumbuita park, Bucharest. A single " +
                "2B+G+9 building of 58 apartments, built to nZEB standard, with a ventilated " +
                "facade and commercial units at ground level.",
            PenaltyClause =
                "If the developer fails to deliver within the agreed date plus six months, " +
                "through its own fault, the buyer may withdraw the deposit and receives a " +
                "penalty of 50% of the deposit paid.",
            Parking =
                "Parking on this project is bought directly from the developer, separately " +
                "from the apartment."
        });

        // 15% advance on the developer price (total minus cesiune), 19% VAT on both the
        // apartment and the assignment. Two payments, numbered 1 and 2. The source spreadsheet
        // labelled the second one "Transa 3", a numbering slip in the sheet rather than a name.
        //
        // The prospectus advertises "~14%". That is 14% of the gross total; the spreadsheet's
        // arithmetic is exactly 15% of (total - cesiune), on every row. The sheet wins.
        project.PricingRules.Add(new PricingRule
        {
            AdvancePct = 0.1500m,
            CesiuneVatPct = 0.1900m,
            VatStandard = 0.1900m,
            VatReduced = 0.0900m,
            Tranches =
            [
                new TrancheDefinition("Transa 1", TrancheBasis.AdvancePercentage, true, TrancheDue.AtSigning),
                new TrancheDefinition("Transa 2", TrancheBasis.Remainder, false, TrancheDue.AtDelivery)
            ]
        });

        // The catalogue is portal-wide, so a type another project already introduced is reused
        // rather than added again — the same shape as the developer lookup above.
        var unitTypes = await db.UnitTypes
            .Include(t => t.Translations)
            .ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, ct);

        var order = unitTypes.Count;

        UnitType TypeFor(string name)
        {
            if (unitTypes.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var created = new UnitType { Name = name, SortOrder = order++ };

            // The canonical name stays as Hubix writes it; only the display name is translated.
            if (EnglishUnitTypeNames.TryGetValue(name, out var english))
            {
                created.Translations.Add(new UnitTypeTranslation
                {
                    Culture = Cultures.English,
                    DisplayName = english
                });
            }

            unitTypes[name] = created;
            db.UnitTypes.Add(created);
            return created;
        }

        foreach (var row in sheet.Rows)
        {
            var code = row.ByHeader("Cod Apartament");
            if (string.IsNullOrWhiteSpace(code))
            {
                Console.WriteLine($"  ! row {row.SourceRow}: no apartment code, skipped");
                continue;
            }

            var typeName = row.ByHeader("Tip Apartament") ?? "Nespecificat";
            var available = string.Equals(row.ByHeader("Disponibil?"), "DA", StringComparison.OrdinalIgnoreCase);

            project.Units.Add(new Unit
            {
                Crt = row.IntByHeader("Nr Crt", "Crt"),
                Code = code.Trim(),
                Building = row.ByHeader("Bloc"),
                Stair = row.ByHeader("Scara"),
                FloorLevel = ParseFloorLevel(row.ByHeader("Etaj")),
                UnitType = TypeFor(typeName.Trim()),
                Orientation = row.ByHeader("Orientare"),
                AreaBuilt = row.DecimalByHeader("MPC"),
                AreaUsable = row.DecimalByHeader("MPU") ?? 0m,
                AreaBalcony = row.DecimalByHeader("MP Balcon/Logie", "MP Balcon", "MPU Balcon") ?? 0m,
                BasePriceNet = row.DecimalByHeader("Pret total apartament fara TVA") ?? 0m,
                CesiunePriceNet = row.DecimalByHeader("Pret cesiune faraTVA", "Pret cesiune fara TVA"),
                ReducedVatEligible = string.Equals(row.ByHeader("TVA redus?"), "DA", StringComparison.OrdinalIgnoreCase),

                // The sheet's DA/NU is the only availability signal it carries. Everything
                // beyond Available needs a reservation record, which the sheet does not have.
                State = available ? UnitState.Available : UnitState.Reserved
            });
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return project.Units.Count;
    }

    /// <summary>"Etaj 7" becomes 7. Numeric, so floor 10 does not sort between 1 and 2.</summary>
    private static int? ParseFloorLevel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var level) ? level : null;
    }
}
