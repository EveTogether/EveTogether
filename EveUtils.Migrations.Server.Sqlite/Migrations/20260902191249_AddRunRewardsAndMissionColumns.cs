using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddRunRewardsAndMissionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "RunParameter",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BonusWindowSeconds",
                table: "RunParameter",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemTypeId",
                table: "RunParameter",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AgentId",
                table: "Run",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MissionLevel",
                table: "Run",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SiteTypeSource",
                table: "Run",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Amount",
                table: "RunParameter");

            migrationBuilder.DropColumn(
                name: "BonusWindowSeconds",
                table: "RunParameter");

            migrationBuilder.DropColumn(
                name: "ItemTypeId",
                table: "RunParameter");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "MissionLevel",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "SiteTypeSource",
                table: "Run");
        }
    }
}
