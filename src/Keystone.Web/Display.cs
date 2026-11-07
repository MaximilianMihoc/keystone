using System.Globalization;
using Keystone.Domain;
using Keystone.Domain.Pricing;
using Microsoft.Extensions.Localization;

namespace Keystone.Web;

/// <summary>
/// Formatting shared by the public pages.
/// </summary>
/// <remarks>
/// Money is stored culture-invariant and only formatted here. Romanian inverts the separators
/// (134.859,00 against 134,859.00), so this must never be done by string concatenation.
/// </remarks>
public static class Display
{
    /// <summary>
    /// The timezone every deadline this portal states is stated in.
    /// </summary>
    /// <remarks>
    /// Not the server's. <c>ToLocalTime()</c> converts to wherever the process happens to run,
    /// which on Linux App Service is UTC, so a buyer in Bucharest was told a deadline two or
    /// three hours earlier than the real one — and the bug was invisible on a Windows
    /// development machine, where the server's local time already is Romanian.
    /// <para>
    /// The IANA id works on Windows as well as Linux; .NET resolves both spellings through ICU.
    /// </para>
    /// </remarks>
    private static readonly TimeZoneInfo Romania =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    /// <summary>The same instant, read as a clock on a wall in Romania.</summary>
    public static DateTimeOffset InRomania(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Romania);

    /// <summary>
    /// A deadline, in Romanian time and saying which time that is.
    /// </summary>
    /// <remarks>
    /// The zone is named because a bare "14:00" in an inbox is unreadable — nothing else in an
    /// email tells the reader which clock it means, and a hold that lapses is not a thing to be
    /// vague about.
    /// </remarks>
    public static string Moment(DateTimeOffset value, string format, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        return InRomania(value).ToString(format, CultureInfo.CurrentCulture)
               + " " + localizer["Time.Zone"].Value;
    }

    /// <summary>A price, in whole euro. Cents are noise at these amounts.</summary>
    public static string Money(decimal value) =>
        "€" + value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A price with cents, for tranche lines where the total must reconcile.</summary>
    public static string MoneyExact(decimal value) =>
        "€" + value.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>Square metres, as the operator writes them.</summary>
    public static string Area(decimal value) =>
        value.ToString("N2", CultureInfo.CurrentCulture) + " mp";

    /// <summary>Resource key for a unit state, so states are translated not stringified.</summary>
    public static string StateKey(UnitState state) => $"State.{state}";

    /// <summary>Resource key for a kind of parking space, for the same reason.</summary>
    public static string ParkingSpaceKey(ParkingSpaceKind kind) => $"Parking.{kind}";

    /// <summary>
    /// Which floor of the car park, in words.
    /// </summary>
    /// <remarks>
    /// Levels are stored signed, so underground is negative. A bare "-1" beside a price reads
    /// like a discount, so the sign becomes a word and the number is shown unsigned.
    /// </remarks>
    public static string ParkingLevel(int level, IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        return level == 0
            ? localizer["Parking.LevelGround"].Value
            : localizer[level < 0 ? "Parking.LevelUnder" : "Parking.LevelAbove",
                Math.Abs(level).ToString(CultureInfo.CurrentCulture)].Value;
    }

    /// <summary>Resource key for when a tranche falls due.</summary>
    public static string DueKey(TrancheDue due) => due switch
    {
        TrancheDue.AtSigning => "Tranche.DueAtSigning",
        TrancheDue.Interim => "Tranche.DueInterim",
        TrancheDue.AtDelivery => "Tranche.DueAtDelivery",
        _ => "Tranche.DueInterim"
    };

    /// <summary>
    /// Only <see cref="UnitState.Available"/> is offered to the public as free. A held unit is
    /// somebody else's for the next two days, and showing it as available would invite a race
    /// the buyer always loses.
    /// </summary>
    public static bool IsFree(UnitState state) => state is UnitState.Available;

    /// <summary>
    /// Where the "open in maps" button goes. A project's own link wins; without one, a Google
    /// Maps search for the project stands in, which resolves anywhere and needs no link the operator
    /// has not given us.
    /// </summary>
    public static string MapsLink(string? mapsUrl, string name, string city) =>
        mapsUrl is { Length: > 0 } url
            ? url
            : "https://www.google.com/maps/search/?api=1&query="
              + Uri.EscapeDataString($"{name}, {city}");
}
