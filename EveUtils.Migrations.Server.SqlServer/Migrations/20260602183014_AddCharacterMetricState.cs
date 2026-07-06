using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterMetricState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterMetricState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CharacterName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BountyTotal = table.Column<long>(type: "bigint", nullable: false),
                    Kills = table.Column<int>(type: "int", nullable: false),
                    MinedJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterMetricState", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterMetricState_CharacterName",
                table: "CharacterMetricState",
                column: "CharacterName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterMetricState");
        }
    }
}
