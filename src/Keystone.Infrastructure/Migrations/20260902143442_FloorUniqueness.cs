using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FloorUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Floors_ProjectId_Building_Level",
                table: "Floors");

            migrationBuilder.CreateIndex(
                name: "UX_Floors_OnePerLevel",
                table: "Floors",
                columns: new[] { "ProjectId", "Building", "Level" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Floors_OnePerLevel",
                table: "Floors");

            migrationBuilder.CreateIndex(
                name: "IX_Floors_ProjectId_Building_Level",
                table: "Floors",
                columns: new[] { "ProjectId", "Building", "Level" },
                unique: true,
                filter: "[Building] IS NOT NULL");
        }
    }
}
