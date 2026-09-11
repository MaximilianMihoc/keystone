using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DeletionRequestedAt",
                table: "Prospects",
                newName: "AnonymisedAt");

            migrationBuilder.CreateTable(
                name: "PrivacyRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyRequests_CompletedAt_RequestedAt",
                table: "PrivacyRequests",
                columns: new[] { "CompletedAt", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyRequests_Email",
                table: "PrivacyRequests",
                column: "Email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivacyRequests");

            migrationBuilder.RenameColumn(
                name: "AnonymisedAt",
                table: "Prospects",
                newName: "DeletionRequestedAt");
        }
    }
}
