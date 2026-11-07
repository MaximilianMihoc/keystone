using System.Text.Json;
using Keystone.Web.Api;
using Shouldly;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Keystone.Tests;

/// <summary>
/// The admin client sends the buyer type by name, the way the board receives it.
/// </summary>
/// <remarks>
/// The service tests pass the enum directly and so never see the wire. These read the body with
/// the options minimal APIs use, which is where changing the buyer type on the board broke: the
/// request bound an enum, and a name was rejected before any code ran. Like every other enum the
/// API takes, it arrives as a string and the endpoint parses it.
/// </remarks>
public class BuyerTypeJsonTests
{
    private static readonly JsonSerializerOptions Options = new HttpJsonOptions().SerializerOptions;

    [Fact]
    public void An_edit_reads_the_buyer_type_by_name() =>
        JsonSerializer.Deserialize<ReservationApi.EditRequest>("""{"buyerType":"Company"}""", Options)!
            .BuyerType.ShouldBe("Company");

    [Fact]
    public void Starting_a_reservation_reads_the_buyer_type_by_name() =>
        JsonSerializer.Deserialize<ReservationApi.StartRequest>("""{"buyerType":"Company"}""", Options)!
            .BuyerType.ShouldBe("Company");
}
