using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StaffLoginTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaffLoginTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StaffUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RequestedFromIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffLoginTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffLoginTokens_StaffUsers_StaffUserId",
                        column: x => x.StaffUserId,
                        principalTable: "StaffUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffLoginTokens_ExpiresAt",
                table: "StaffLoginTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_StaffLoginTokens_StaffUserId",
                table: "StaffLoginTokens",
                column: "StaffUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffLoginTokens_TokenHash",
                table: "StaffLoginTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffLoginTokens");
        }
    }
}
