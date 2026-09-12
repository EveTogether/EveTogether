using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.SqlServer.Migrations
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
                type: "nvarchar(max)",
                nullable: true);
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
