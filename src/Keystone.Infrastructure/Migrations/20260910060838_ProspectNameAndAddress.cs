using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Keystone.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProspectNameAndAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Prospects",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Prospects",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "Prospects",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Surname",
                table: "Prospects",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReservationNotice",
                table: "ProjectTranslations",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            // FullName stops being a column anybody writes and becomes the two parts joined,
            // so the names already collected have to be split before the column is dropped.
            // First word to FirstName, the rest to Surname; a single word stays a first name.
            // Romanian names are often three words and this will file some of them wrong —
            // which is why the admin screen can now correct them.
            migrationBuilder.Sql("""
                UPDATE [Prospects]
                SET [FirstName] = CASE
                        WHEN CHARINDEX(' ', LTRIM(RTRIM([FullName]))) = 0 THEN LTRIM(RTRIM([FullName]))
                        ELSE LEFT(LTRIM(RTRIM([FullName])), CHARINDEX(' ', LTRIM(RTRIM([FullName]))) - 1)
                    END,
                    [Surname] = CASE
                        WHEN CHARINDEX(' ', LTRIM(RTRIM([FullName]))) = 0 THEN NULL
                        ELSE LTRIM(SUBSTRING(LTRIM(RTRIM([FullName])), CHARINDEX(' ', LTRIM(RTRIM([FullName]))) + 1, 200))
                    END
                WHERE [FullName] IS NOT NULL AND LTRIM(RTRIM([FullName])) <> '';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                table: "Prospects",
                type: "nvarchar(201)",
                maxLength: 201,
                nullable: true,
                computedColumnSql: "NULLIF(LTRIM(RTRIM(CONCAT([FirstName], ' ', [Surname]))), '')",
                stored: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // FullName goes back to a plain column first. SQL Server will not drop a column a
            // computed column is defined over, so the order here is not cosmetic. The joined
            // name is parked in a temporary column and put back afterwards, because a rollback
            // that erases every buyer's name is not a rollback anybody would risk running.
            migrationBuilder.Sql("""
                ALTER TABLE [Prospects] ADD [FullNameWas] nvarchar(200) NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE [Prospects] SET [FullNameWas] = LEFT([FullName], 200);
                """);

            migrationBuilder.AlterColumn<string>(
                name: "FullName",
                table: "Prospects",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(201)",
                oldMaxLength: 201,
                oldNullable: true,
                oldComputedColumnSql: "NULLIF(LTRIM(RTRIM(CONCAT([FirstName], ' ', [Surname]))), '')");

            migrationBuilder.Sql("""
                UPDATE [Prospects] SET [FullName] = [FullNameWas];
                ALTER TABLE [Prospects] DROP COLUMN [FullNameWas];
                """);

            migrationBuilder.DropColumn(
                name: "City",
                table: "Prospects");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "Prospects");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "Prospects");

            migrationBuilder.DropColumn(
                name: "Surname",
                table: "Prospects");

            migrationBuilder.DropColumn(
                name: "ReservationNotice",
                table: "ProjectTranslations");
        }
    }
}
