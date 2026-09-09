using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnitTypesArePortalWide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two projects could each own a row called "2 camere", and the unique index below
            // would refuse them. Fold every same-named row onto one survivor first, moving the
            // apartments and waiting lists across, so the index has something to bite on.
            // MIN(Id) only has to pick the same row every time, not a meaningful one.
            migrationBuilder.Sql("""
                UPDATE u
                SET u.UnitTypeId = k.KeepId
                FROM Units u
                JOIN UnitTypes t ON t.Id = u.UnitTypeId
                JOIN (SELECT Name, MIN(Id) AS KeepId FROM UnitTypes GROUP BY Name) k
                  ON k.Name = t.Name
                WHERE u.UnitTypeId <> k.KeepId;

                UPDATE w
                SET w.UnitTypeId = k.KeepId
                FROM WaitlistEntries w
                JOIN UnitTypes t ON t.Id = w.UnitTypeId
                JOIN (SELECT Name, MIN(Id) AS KeepId FROM UnitTypes GROUP BY Name) k
                  ON k.Name = t.Name
                WHERE w.UnitTypeId <> k.KeepId;

                DELETE tr
                FROM UnitTypeTranslations tr
                JOIN UnitTypes t ON t.Id = tr.UnitTypeId
                WHERE t.Id <> (SELECT MIN(x.Id) FROM UnitTypes x WHERE x.Name = t.Name);

                DELETE t
                FROM UnitTypes t
                WHERE t.Id <> (SELECT MIN(x.Id) FROM UnitTypes x WHERE x.Name = t.Name);
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_UnitTypes_Projects_ProjectId",
                table: "UnitTypes");

            migrationBuilder.DropIndex(
                name: "IX_UnitTypes_ProjectId_Name",
                table: "UnitTypes");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "UnitTypes");

            migrationBuilder.CreateIndex(
                name: "IX_UnitTypes_Name",
                table: "UnitTypes",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The column comes back empty and the merge above is not undone: rows folded together
        /// cannot be told apart again. Down restores the shape, not the old data.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UnitTypes_Name",
                table: "UnitTypes");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "UnitTypes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_UnitTypes_ProjectId_Name",
                table: "UnitTypes",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_UnitTypes_Projects_ProjectId",
                table: "UnitTypes",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
