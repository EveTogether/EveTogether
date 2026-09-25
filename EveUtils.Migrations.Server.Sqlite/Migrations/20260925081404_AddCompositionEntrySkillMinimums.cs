using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositionEntrySkillMinimums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FleetCompositionEntrySkillMinimum",
                columns: table => new
                {
                    SkillTypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetCompositionEntrySkillMinimum", x => new { x.EntryId, x.SkillTypeId });
                    table.ForeignKey(
                        name: "FK_FleetCompositionEntrySkillMinimum_FleetCompositionEntry_EntryId",
                        column: x => x.EntryId,
                        principalTable: "FleetCompositionEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FleetCompositionEntrySkillMinimum");
        }
    }
}
