using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hubix.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WaitlistAccessToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The empty default is safe only because nothing has ever inserted into this
            // table: joining a waiting list is this migration's own feature. A blank token
            // would otherwise be a shared credential, and the unique index would refuse the
            // second row anyway.
            migrationBuilder.AddColumn<string>(
                name: "AccessToken",
                table: "WaitlistEntries",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "UX_Waitlist_AccessToken",
                table: "WaitlistEntries",
                column: "AccessToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Waitlist_AccessToken",
                table: "WaitlistEntries");

            migrationBuilder.DropColumn(
                name: "AccessToken",
                table: "WaitlistEntries");
        }
    }
}
