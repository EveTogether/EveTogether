using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddHomefrontPayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HomefrontCompletedWaveCount",
                table: "Run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HomefrontOutcome",
                table: "Run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HomefrontPayoutTableVersion",
                table: "Run",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HomefrontCompletedWaveCount",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "HomefrontOutcome",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "HomefrontPayoutTableVersion",
                table: "Run");
        }
    }
}
