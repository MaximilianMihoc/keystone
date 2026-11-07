using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ParkingSpacePriceSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SnapDepositNet",
                table: "ParkingSpaces",
                type: "decimal(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SnapPriceNet",
                table: "ParkingSpaces",
                type: "decimal(12,2)",
                nullable: true);

            // Spaces already on a sale have no per-space figure, and without one, taking such a
            // space off a reservation would subtract nothing and leave its total overstated.
            // The price for their kind is the best answer available: the reservation total was
            // built from exactly that, and nothing has repriced in between on any live data.
            migrationBuilder.Sql(
                """
                UPDATE s
                SET s.SnapPriceNet = p.PriceNet,
                    s.SnapDepositNet = p.DepositNet
                FROM ParkingSpaces s
                JOIN ParkingPrices p
                  ON p.ProjectId = s.ProjectId AND p.Kind = s.Kind
                WHERE s.ReservationId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SnapDepositNet",
                table: "ParkingSpaces");

            migrationBuilder.DropColumn(
                name: "SnapPriceNet",
                table: "ParkingSpaces");
        }
    }
}
