using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProjectTranslationCity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "ProjectTranslations",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "City",
                table: "ProjectTranslations");
        }
    }
}
