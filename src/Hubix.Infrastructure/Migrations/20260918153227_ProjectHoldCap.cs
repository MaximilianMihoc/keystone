using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProjectHoldCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxActiveHoldsPerProspect",
                table: "Projects",
                type: "int",
                nullable: false,
                // Existing projects keep the cap they had when it was a constant.
                defaultValue: 3);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Projects_MaxActiveHoldsPerProspect",
                table: "Projects",
                sql: "[MaxActiveHoldsPerProspect] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Projects_MaxActiveHoldsPerProspect",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "MaxActiveHoldsPerProspect",
                table: "Projects");
        }
    }
}
