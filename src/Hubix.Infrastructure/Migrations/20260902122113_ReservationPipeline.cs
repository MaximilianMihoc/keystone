using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReservationPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgreementSentAt",
                table: "Reservations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "Reservations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SnapReservationFee",
                table: "Reservations",
                type: "decimal(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StateChangedAt",
                table: "Reservations",
                type: "datetimeoffset",
                nullable: false,
                defaultValueSql: "SYSDATETIMEOFFSET()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgreementSentAt",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "SnapReservationFee",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "StateChangedAt",
                table: "Reservations");
        }
    }
}
