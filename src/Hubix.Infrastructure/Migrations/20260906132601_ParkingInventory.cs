using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ParkingInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParkingAdvanceNet",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "ParkingAvailable",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "ParkingPriceNet",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "ParkingSpaceKind",
                table: "Units");

            migrationBuilder.AddColumn<decimal>(
                name: "SnapParkingDepositNet",
                table: "Reservations",
                type: "decimal(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SnapParkingNet",
                table: "Reservations",
                type: "decimal(12,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MaxParkingSpacesPerReservation",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ParkingPrices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PriceNet = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    DepositNet = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParkingPrices_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ParkingSpaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StateChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HoldId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AdminNotes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingSpaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParkingSpaces_Holds_HoldId",
                        column: x => x.HoldId,
                        principalTable: "Holds",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ParkingSpaces_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ParkingSpaces_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalTable: "Reservations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "UX_ParkingPrices_ProjectKind",
                table: "ParkingPrices",
                columns: new[] { "ProjectId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpaces_HoldId",
                table: "ParkingSpaces",
                column: "HoldId");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpaces_ProjectId_State_Kind",
                table: "ParkingSpaces",
                columns: new[] { "ProjectId", "State", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpaces_ReservationId",
                table: "ParkingSpaces",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "UX_ParkingSpaces_ProjectCode",
                table: "ParkingSpaces",
                columns: new[] { "ProjectId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParkingPrices");

            migrationBuilder.DropTable(
                name: "ParkingSpaces");

            migrationBuilder.DropColumn(
                name: "SnapParkingDepositNet",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "SnapParkingNet",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "MaxParkingSpacesPerReservation",
                table: "Projects");

            migrationBuilder.AddColumn<decimal>(
                name: "ParkingAdvanceNet",
                table: "Units",
                type: "decimal(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ParkingAvailable",
                table: "Units",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ParkingPriceNet",
                table: "Units",
                type: "decimal(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParkingSpaceKind",
                table: "Units",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);
        }
    }
}
