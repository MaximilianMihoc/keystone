using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReservationConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Reservations_OneOpenPerUnit",
                table: "Reservations");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Reservations",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Reservations_OneOpenPerUnit",
                table: "Reservations",
                column: "UnitId",
                unique: true,
                filter: "[State] <> 'Cancelled'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reservations_CancelledAgrees",
                table: "Reservations",
                sql: "([State] = 'Cancelled' AND [CancelledAt] IS NOT NULL) OR ([State] <> 'Cancelled' AND [CancelledAt] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Reservations_OneOpenPerUnit",
                table: "Reservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reservations_CancelledAgrees",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Reservations");

            migrationBuilder.CreateIndex(
                name: "UX_Reservations_OneOpenPerUnit",
                table: "Reservations",
                column: "UnitId",
                unique: true,
                filter: "[CancelledAt] IS NULL AND [State] <> 'Cancelled'");
        }
    }
}
