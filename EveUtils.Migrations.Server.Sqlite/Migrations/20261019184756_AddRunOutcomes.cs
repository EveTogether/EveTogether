using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddRunOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunOutcome",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivityId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContributorBucket = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    KeyCode = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    StatsKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SiteTypeSource = table.Column<int>(type: "INTEGER", nullable: true),
                    TypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    MissionLevel = table.Column<int>(type: "INTEGER", nullable: true),
                    AbyssalTier = table.Column<int>(type: "INTEGER", nullable: true),
                    AbyssalWeather = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CompletedOn = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    FixedIsk = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    FleetSize = table.Column<int>(type: "INTEGER", nullable: false),
                    LootBasis = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenUnrecognisedLootCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunOutcome", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunOutcomeLootLine",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunOutcomeId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunOutcomeLootLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunOutcomeLootLine_RunOutcome_RunOutcomeId",
                        column: x => x.RunOutcomeId,
                        principalTable: "RunOutcome",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunOutcome_ActivityId",
                table: "RunOutcome",
                column: "ActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunOutcome_CompletedOn",
                table: "RunOutcome",
                column: "CompletedOn");

            migrationBuilder.CreateIndex(
                name: "IX_RunOutcome_ContributorBucket",
                table: "RunOutcome",
                column: "ContributorBucket");

            migrationBuilder.CreateIndex(
                name: "IX_RunOutcome_KeyCode_CompletedOn",
                table: "RunOutcome",
                columns: new[] { "KeyCode", "CompletedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_RunOutcomeLootLine_RunOutcomeId",
                table: "RunOutcomeLootLine",
                column: "RunOutcomeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunOutcomeLootLine");

            migrationBuilder.DropTable(
                name: "RunOutcome");
        }
    }
}
