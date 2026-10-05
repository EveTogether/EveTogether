using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SkillPlan",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillPlan", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SkillPlanRow",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillTypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SourceLabel = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillPlanRow", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SkillPlan_CharacterId",
                table: "SkillPlan",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_SkillPlanRow_PlanId_Position",
                table: "SkillPlanRow",
                columns: new[] { "PlanId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_SkillPlanRow_PlanId_SkillTypeId_Level",
                table: "SkillPlanRow",
                columns: new[] { "PlanId", "SkillTypeId", "Level" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkillPlan");

            migrationBuilder.DropTable(
                name: "SkillPlanRow");
        }
    }
}
