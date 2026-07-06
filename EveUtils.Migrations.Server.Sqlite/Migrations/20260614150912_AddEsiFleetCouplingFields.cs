using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddEsiFleetCouplingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EsiWingId",
                table: "FleetWing",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EsiSquadId",
                table: "FleetSquad",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EsiMemberId",
                table: "FleetMember",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EsiSyncState",
                table: "Fleet",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EsiWingId",
                table: "FleetWing");

            migrationBuilder.DropColumn(
                name: "EsiSquadId",
                table: "FleetSquad");

            migrationBuilder.DropColumn(
                name: "EsiMemberId",
                table: "FleetMember");

            migrationBuilder.DropColumn(
                name: "EsiSyncState",
                table: "Fleet");
        }
    }
}
