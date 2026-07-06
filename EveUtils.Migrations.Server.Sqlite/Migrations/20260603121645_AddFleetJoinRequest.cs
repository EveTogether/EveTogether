using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetJoinRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FleetJoinRequest",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FleetId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequesterCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RespondedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetJoinRequest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetJoinRequest_Fleet_FleetId",
                        column: x => x.FleetId,
                        principalTable: "Fleet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FleetJoinRequest_FleetId_Status",
                table: "FleetJoinRequest",
                columns: new[] { "FleetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FleetJoinRequest_RequesterCharacterId_Status",
                table: "FleetJoinRequest",
                columns: new[] { "RequesterCharacterId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FleetJoinRequest");
        }
    }
}
