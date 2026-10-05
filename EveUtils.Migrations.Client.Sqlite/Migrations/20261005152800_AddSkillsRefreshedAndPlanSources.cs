using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillsRefreshedAndPlanSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SkillsRefreshedAt",
                table: "CharacterAttributes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SkillPlanSource",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DroppedLevels = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillPlanSource", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkillPlanSource_PlanId",
                table: "SkillPlanSource",
                column: "PlanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkillPlanSource");

            migrationBuilder.DropColumn(
                name: "SkillsRefreshedAt",
                table: "CharacterAttributes");
        }
    }
}
