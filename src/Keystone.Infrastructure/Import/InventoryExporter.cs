using ClosedXML.Excel;
using Keystone.Domain;
using Keystone.Domain.Entities;
using Keystone.Domain.Pricing;
using Microsoft.EntityFrameworkCore;

namespace Keystone.Infrastructure.Import;

/// <summary>
/// Writes a project back out as the workbook the operator already knows.
/// </summary>
/// <remarks>
/// <para>
/// Their own column names, in their own order, so a file that comes out of here can be sent to
/// a developer or opened next to last month's without anybody having to re-learn it. Leaving
/// with your data is not a feature to be talked out of.
/// </para>
/// <para>
/// Tranche columns are calculated on the way out rather than stored, which is the point: in the
/// spreadsheets this replaces they are typed by hand, and that is why some rows lost their
/// formula and no two projects agree.
/// </para>
/// </remarks>
public sealed class InventoryExporter(KeystoneDbContext db)
{
    public async Task<byte[]> ExportAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects
            .AsNoTracking()
            .Include(p => p.PricingRules)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");

        var rule = project.PricingRules
            .Where(r => r.EffectiveFrom <= DateTimeOffset.UtcNow)
            .MaxBy(r => r.EffectiveFrom);

        var units = await db.Units
            .AsNoTracking()
            .Where(u => u.ProjectId == projectId)
            .OrderBy(u => u.Crt ?? int.MaxValue)
            .ThenBy(u => u.FloorLevel)
            .ThenBy(u => u.Code)
            .Select(u => new
            {
                u.Crt,
                u.Code,
                u.Building,
                u.Stair,
                u.FloorLevel,
                TypeName = u.UnitType!.Name,
                u.Orientation,
                u.AreaBuilt,
                u.AreaUsable,
                u.AreaBalcony,
                u.AreaTotal,
                u.BasePriceNet,
                u.CesiunePriceNet,
                u.ReducedVatEligible,
                u.State,
                u.AdminNotes,
                Reservation = u.Reservations
                    .Where(r => r.State != ReservationState.Cancelled)
                    .Select(r => new
                    {
                        r.ContractNumber,
                        r.FeePaidAt,
                        r.SignedAgreementAt,
                        r.CesiuneSignedAt,
                        r.NotaryLocation,
                        r.InternalNotes
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Apartamente");

        // The operator's headings, verbatim. The tranche columns are named from each project's own
        // stored labels, which is why the label is stored at all — the public pages number the
        // payments by position instead. Linden's sheet numbered its second payment "Transa 3";
        // that slip is corrected here, so this export no longer matches that column name.
        string[] headers =
        [
            "Nr Crt", "Disponibil?", "Bloc", "Scara", "Etaj", "Cod Apartament", "Tip Apartament",
            "Orientare", "MPC", "MPU", "MP Balcon/Logie", "Suprafata utila totala",
            "Pret total apartament fara TVA", "TVA redus?", "Pret cesiune faraTVA",
            .. TrancheHeaders(rule),
            "Numar contract", "Platit rezervare", "Trimis rezervarea semnata",
            "Cesiune semnata la NOTAR", "Locatie cesiune", "Comentarii"
        ];

        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        var row = 2;

        foreach (var unit in units)
        {
            var tranches = rule is not null && unit.BasePriceNet > 0
                ? Schedule(rule, unit.BasePriceNet, unit.CesiunePriceNet ?? 0m, unit.ReducedVatEligible)
                : [];

            var column = 1;

            Set(sheet, row, ref column, unit.Crt);
            Set(sheet, row, ref column, unit.State == UnitState.Available ? "DA" : "NU");
            Set(sheet, row, ref column, unit.Building);
            Set(sheet, row, ref column, unit.Stair);
            Set(sheet, row, ref column, unit.FloorLevel);
            Set(sheet, row, ref column, unit.Code);
            Set(sheet, row, ref column, unit.TypeName);
            Set(sheet, row, ref column, unit.Orientation);
            Set(sheet, row, ref column, unit.AreaBuilt);
            Set(sheet, row, ref column, unit.AreaUsable);
            Set(sheet, row, ref column, unit.AreaBalcony);
            Set(sheet, row, ref column, unit.AreaTotal);
            Set(sheet, row, ref column, unit.BasePriceNet);
            Set(sheet, row, ref column, unit.ReducedVatEligible ? "DA" : "NU");
            Set(sheet, row, ref column, unit.CesiunePriceNet);

            foreach (var tranche in tranches)
            {
                Set(sheet, row, ref column, tranche.Gross);
            }

            // A rule with no tranches still has to line the remaining columns up.
            for (var i = tranches.Count; i < TrancheHeaders(rule).Count; i++)
            {
                column++;
            }

            Set(sheet, row, ref column, unit.Reservation?.ContractNumber);
            Set(sheet, row, ref column, unit.Reservation?.FeePaidAt);
            Set(sheet, row, ref column, unit.Reservation?.SignedAgreementAt);
            Set(sheet, row, ref column, unit.Reservation?.CesiuneSignedAt);
            Set(sheet, row, ref column, unit.Reservation?.NotaryLocation);
            Set(sheet, row, ref column, unit.AdminNotes);

            row++;
        }

        sheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static IReadOnlyList<string> TrancheHeaders(PricingRule? rule) =>
        rule is null ? [] : rule.Tranches.Select(t => $"{t.Label} apartament").ToList();

    private static IReadOnlyList<TrancheAmount> Schedule(
        PricingRule rule,
        decimal basePrice,
        decimal cesiune,
        bool reducedVat)
    {
        if (rule.Tranches.Count == 0 || cesiune > basePrice)
        {
            return [];
        }

        return TrancheCalculator.Calculate(rule, basePrice, cesiune, reducedVat).Tranches;
    }

    private static void Set(IXLWorksheet sheet, int row, ref int column, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case DateTimeOffset at:
                sheet.Cell(row, column).Value = at.UtcDateTime.Date;
                sheet.Cell(row, column).Style.DateFormat.Format = "dd.mm.yyyy";
                break;
            case decimal number:
                sheet.Cell(row, column).Value = number;
                sheet.Cell(row, column).Style.NumberFormat.Format = "#,##0.00";
                break;
            case int whole:
                sheet.Cell(row, column).Value = whole;
                break;
            default:
                sheet.Cell(row, column).Value = value.ToString();
                break;
        }

        column++;
    }
}
