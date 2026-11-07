using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ParkingInformation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParkingSpaceKind",
                table: "Units",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Parking",
                table: "ProjectTranslations",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParkingSpaceKind",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "Parking",
                table: "ProjectTranslations");
        }
    }
}
