using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.PostgreSql.Migrations
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
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EsiSquadId",
                table: "FleetSquad",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EsiMemberId",
                table: "FleetMember",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EsiSyncState",
                table: "Fleet",
                type: "integer",
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
