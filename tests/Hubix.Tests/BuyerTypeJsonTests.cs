using System.Text.Json;
using Hubix.Domain;
using Hubix.Infrastructure.Sales;
using Hubix.Web.Api;
using Shouldly;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Hubix.Tests;

/// <summary>
/// The admin client sends the buyer type by name, the way the board receives it.
/// </summary>
/// <remarks>
/// The service tests pass the enum directly and so never see the wire. These read the body with
/// the options minimal APIs use, which is where changing the buyer type on the board broke.
/// </remarks>
public class BuyerTypeJsonTests
{
    private static readonly JsonSerializerOptions Options = new HttpJsonOptions().SerializerOptions;

    [Fact]
    public void An_edit_reads_the_buyer_type_by_name() =>
        JsonSerializer.Deserialize<ReservationEdit>("""{"buyerType":"Company"}""", Options)!
            .BuyerType.ShouldBe(BuyerType.Company);

    [Fact]
    public void Starting_a_reservation_reads_the_buyer_type_by_name() =>
        JsonSerializer.Deserialize<ReservationApi.StartRequest>("""{"buyerType":"Company"}""", Options)!
            .BuyerType.ShouldBe(BuyerType.Company);
}
