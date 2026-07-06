using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    FleetId = table.Column<long>(type: "bigint", nullable: false),
                    RequesterCharacterId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    RespondedAt = table.Column<long>(type: "bigint", nullable: true)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
