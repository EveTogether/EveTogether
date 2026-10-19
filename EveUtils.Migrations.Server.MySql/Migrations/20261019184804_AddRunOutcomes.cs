using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ActivityId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContributorBucket = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    KeyCode = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StatsKind = table.Column<int>(type: "int", nullable: false),
                    SiteTypeSource = table.Column<int>(type: "int", nullable: true),
                    TypeId = table.Column<int>(type: "int", nullable: false),
                    NormalizedName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MissionLevel = table.Column<int>(type: "int", nullable: true),
                    AbyssalTier = table.Column<int>(type: "int", nullable: true),
                    AbyssalWeather = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CompletedOn = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DurationSeconds = table.Column<int>(type: "int", nullable: true),
                    FixedIsk = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FleetSize = table.Column<int>(type: "int", nullable: false),
                    LootBasis = table.Column<int>(type: "int", nullable: false),
                    OpenUnrecognisedLootCount = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunOutcome", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RunOutcomeLootLine",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RunOutcomeId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TypeId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
