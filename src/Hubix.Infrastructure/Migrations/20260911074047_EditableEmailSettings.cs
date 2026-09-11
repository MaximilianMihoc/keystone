using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EditableEmailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SalesInbox",
                table: "Projects",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PortalSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesInbox = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    PublicBaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LiveSendingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RedirectAllTo = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AllowedRecipients = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BlockedRecipients = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortalSettings");

            migrationBuilder.DropColumn(
                name: "SalesInbox",
                table: "Projects");
        }
    }
}
