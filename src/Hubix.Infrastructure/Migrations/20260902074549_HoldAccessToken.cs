using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HoldAccessToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessToken",
                table: "Holds",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "UX_Holds_AccessToken",
                table: "Holds",
                column: "AccessToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Holds_AccessToken",
                table: "Holds");

            migrationBuilder.DropColumn(
                name: "AccessToken",
                table: "Holds");
        }
    }
}
