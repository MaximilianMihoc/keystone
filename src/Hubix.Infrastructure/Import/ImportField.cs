namespace Hubix.Infrastructure.Import;

/// <summary>
/// A column of a Hubix inventory sheet, and what it means here.
/// </summary>
/// <param name="Field">Stable key. Saved mapping profiles store this, so it must not change.</param>
/// <param name="Label">What the mapping screen shows.</param>
/// <param name="Aliases">
/// Header text seen in the real spreadsheets. Five projects have written the same column five
/// ways — <c>MPU Balcon</c>, <c>MP Balcon/Logie</c> — and guessing from these is what makes the
/// mapping screen a review rather than thirty dropdowns to fill in by hand.
/// </param>
/// <param name="Required">Without it a row cannot be identified or priced.</param>
public sealed record ImportField(
    string Field,
    string Label,
    IReadOnlyList<string> Aliases,
    bool Required = false)
{
    public const string Code = "code";
    public const string Crt = "crt";
    public const string Building = "building";
    public const string Stair = "stair";
    public const string Floor = "floor";
    public const string TypeName = "typeName";
    public const string Orientation = "orientation";
    public const string AreaBuilt = "areaBuilt";
    public const string AreaUsable = "areaUsable";
    public const string AreaBalcony = "areaBalcony";
    public const string BasePriceNet = "basePriceNet";
    public const string CesiunePriceNet = "cesiunePriceNet";
    public const string ReducedVat = "reducedVat";
    public const string Available = "available";
    public const string ParkingPriceNet = "parkingPriceNet";
    public const string ParkingAdvanceNet = "parkingAdvanceNet";
    public const string Notes = "notes";

    /// <summary>
    /// Every field the importer can fill, with the header text each has appeared under.
    /// </summary>
    /// <remarks>
    /// Deliberately not everything on a Unit. Nothing here can set state, and there is no
    /// column for a tranche: tranche amounts are calculated from the pricing rule, and letting
    /// a spreadsheet write them back would reintroduce exactly the drift this replaces.
    /// </remarks>
    public static IReadOnlyList<ImportField> All { get; } =
    [
        new(Code, "Apartment code", ["Cod Apartament", "Cod A", "Cod"], Required: true),
        new(Crt, "Row number", ["Nr Crt", "Crt", "Nr"]),
        new(Building, "Block", ["Bloc"]),
        new(Stair, "Stair", ["Scara"]),
        new(Floor, "Floor", ["Etaj"]),
        new(TypeName, "Apartment type", ["Tip Apartament", "Tip"], Required: true),
        new(Orientation, "Orientation", ["Orientare"]),
        new(AreaBuilt, "Built area", ["MPC"]),
        new(AreaUsable, "Usable area", ["MPU", "Suprafata utila"], Required: true),
        new(AreaBalcony, "Balcony area", ["MP Balcon/Logie", "MPU Balcon", "MP Balcon", "Balcon"]),
        new(BasePriceNet, "Price excl. VAT",
            ["Pret total apartament fara TVA", "Pret total fara TVA"], Required: true),
        new(CesiunePriceNet, "Cesiune excl. VAT",
            ["Pret cesiune faraTVA", "Pret cesiune fara TVA", "Pret cesiune faraTVA 21%"]),
        new(ReducedVat, "Reduced VAT", ["TVA redus?", "TVA redus"]),
        new(Available, "Available", ["Disponibil?", "Disponibil"]),
        new(ParkingPriceNet, "Parking excl. VAT", ["Pret total parcare fara TVA"]),
        new(ParkingAdvanceNet, "Parking advance",
            ["Avans + cesiune parcare fara TVA", "Avans parcare"]),
        new(Notes, "Comments", ["Comentarii", "Comments"])
    ];

    public static ImportField? ByKey(string field) =>
        All.FirstOrDefault(f => f.Field == field);

    /// <summary>
    /// The field whose aliases match this header text, if any.
    /// </summary>
    /// <remarks>
    /// Compared with accents and punctuation stripped, because the same column is written
    /// <c>Pret cesiune faraTVA</c> in one sheet and <c>Preț cesiune fără TVA</c> in another.
    /// </remarks>
    public static ImportField? Guess(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || HeaderText.ReadsLikeData(header))
        {
            return null;
        }

        var needle = HeaderText.Normalise(header);

        var exact = All.FirstOrDefault(f =>
            f.Aliases.Any(a => HeaderText.Normalise(a) == needle));

        if (exact is not null)
        {
            return exact;
        }

        // Hubix appends the rate of the day to some headings — "Pret cesiune faraTVA 21%" is
        // the same column as "Pret cesiune faraTVA" — so a trailing number is dropped and the
        // rest must match exactly.
        //
        // Exactly, and not as a prefix: "Etaj 5 - M12_39A - Studio" begins with "Etaj", and a
        // prefix rule silently mapped that ruined Cartierul Vienez cell to the floor column.
        // A wrong guess presented as a confident one is worse than no guess at all.
        var trimmed = needle.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        return trimmed.Length == needle.Length
            ? null
            : All.FirstOrDefault(f => f.Aliases.Any(a => HeaderText.Normalise(a) == trimmed));
    }
}

/// <summary>Comparing header text written by hand across five spreadsheets.</summary>
public static class HeaderText
{
    /// <summary>Lower case, no accents, letters and digits only.</summary>
    public static string Normalise(string value) =>
        new(value.ToLowerInvariant()
            .Replace('ș', 's').Replace('ş', 's')
            .Replace('ț', 't').Replace('ţ', 't')
            .Replace('ă', 'a').Replace('â', 'a').Replace('î', 'i')
            .Where(char.IsLetterOrDigit)
            .ToArray());

    /// <summary>
    /// Whether a header cell is really a row of the table.
    /// </summary>
    /// <remarks>
    /// A number, a date, or a long dashed string like "Etaj 5 - M12_39A - Studio" is data that
    /// landed where a name should be — Cartierul Vienez has exactly that in cell A1.
    /// Recognising it does not repair the file; it stops the column being guessed at, and lets
    /// the mapping screen say plainly that a person has to choose.
    /// </remarks>
    public static bool ReadsLikeData(string header)
    {
        var trimmed = header.Trim();

        if (decimal.TryParse(trimmed, out _) || DateTime.TryParse(trimmed, out _))
        {
            return true;
        }

        // Column names are short. A long string carrying separators is a record.
        return trimmed.Length > 24
               && (trimmed.Contains(" - ", StringComparison.Ordinal) || trimmed.Contains('_'));
    }
}
