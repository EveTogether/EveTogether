using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddRunCombatTimelineSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunCombatTimeline",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Seconds = table.Column<int>(type: "int", nullable: false),
                    MaxHitOut = table.Column<int>(type: "int", nullable: false),
                    MaxHitOutTarget = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    MaxHitIn = table.Column<int>(type: "int", nullable: false),
                    MaxHitInSource = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    HitsOut = table.Column<int>(type: "int", nullable: false),
                    MissesOut = table.Column<int>(type: "int", nullable: false),
                    HitsIn = table.Column<int>(type: "int", nullable: false),
                    MissesIn = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCombatTimeline", x => x.RunId);
                    table.ForeignKey(
                        name: "FK_RunCombatTimeline_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunCombatSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Total = table.Column<long>(type: "bigint", nullable: false),
                    Samples = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCombatSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunCombatSeries_RunCombatTimeline_RunId",
                        column: x => x.RunId,
                        principalTable: "RunCombatTimeline",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunHitTally",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Counterparty = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Weapon = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Quality = table.Column<int>(type: "int", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    Sum = table.Column<long>(type: "bigint", nullable: false),
                    Min = table.Column<int>(type: "int", nullable: false),
                    Max = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunHitTally", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunHitTally_RunCombatTimeline_RunId",
                        column: x => x.RunId,
                        principalTable: "RunCombatTimeline",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunCombatSeries_RunId",
                table: "RunCombatSeries",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunHitTally_RunId",
                table: "RunHitTally",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunCombatSeries");

            migrationBuilder.DropTable(
                name: "RunHitTally");

            migrationBuilder.DropTable(
                name: "RunCombatTimeline");
        }
    }
}
