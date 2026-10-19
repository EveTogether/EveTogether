using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EveUtils.Migrations.Server.PostgreSql.Migrations
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
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActivityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContributorBucket = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyCode = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    StatsKind = table.Column<int>(type: "integer", nullable: false),
                    SiteTypeSource = table.Column<int>(type: "integer", nullable: true),
                    TypeId = table.Column<int>(type: "integer", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    MissionLevel = table.Column<int>(type: "integer", nullable: true),
                    AbyssalTier = table.Column<int>(type: "integer", nullable: true),
                    AbyssalWeather = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CompletedOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    FixedIsk = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FleetSize = table.Column<int>(type: "integer", nullable: false),
                    LootBasis = table.Column<int>(type: "integer", nullable: false),
                    OpenUnrecognisedLootCount = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunOutcome", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunOutcomeLootLine",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunOutcomeId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    TypeId = table.Column<int>(type: "integer", nullable: false),
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
