using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.PostgreSql.Migrations
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
                    SkillTypeId = table.Column<int>(type: "integer", nullable: false),
                    EntryId = table.Column<long>(type: "bigint", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetCompositionEntrySkillMinimum", x => new { x.EntryId, x.SkillTypeId });
                    table.ForeignKey(
                        name: "FK_FleetCompositionEntrySkillMinimum_FleetCompositionEntry_Ent~",
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
