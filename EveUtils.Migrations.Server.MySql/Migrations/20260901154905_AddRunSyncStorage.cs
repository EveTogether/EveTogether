using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddRunSyncStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Run",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    GroupCode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActivityKind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    StoppedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SavedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SiteTypeId = table.Column<int>(type: "int", nullable: false),
                    SiteName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SolarSystemId = table.Column<int>(type: "int", nullable: true),
                    Signature = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Role = table.Column<int>(type: "int", nullable: false),
                    IsPayoutEligible = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    FitContentHash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FitNameSnapshot = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SyncState = table.Column<int>(type: "int", nullable: false),
                    LastPushedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Run", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RunBountyEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Isk = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunBountyEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunBountyEntry_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RunEnemyObservation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    EnemyTypeId = table.Column<int>(type: "int", nullable: false),
                    EnemyName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunEnemyObservation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunEnemyObservation_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RunLootCapture",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunLootCapture", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunLootCapture_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RunParameter",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ParameterKey = table.Column<int>(type: "int", nullable: false),
                    TypedValue = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunParameter", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunParameter_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RunLootEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RunLootCaptureId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ItemTypeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Quantity = table.Column<long>(type: "bigint", nullable: true),
                    Volume = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    ClipboardPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    LootKind = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunLootEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunLootEntry_RunLootCapture_RunLootCaptureId",
                        column: x => x.RunLootCaptureId,
                        principalTable: "RunLootCapture",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Run_GroupCode_CharacterId",
                table: "Run",
                columns: new[] { "GroupCode", "CharacterId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunBountyEntry_RunId",
                table: "RunBountyEntry",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunEnemyObservation_RunId",
                table: "RunEnemyObservation",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunLootCapture_RunId",
                table: "RunLootCapture",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunLootEntry_RunLootCaptureId",
                table: "RunLootEntry",
                column: "RunLootCaptureId");

            migrationBuilder.CreateIndex(
                name: "IX_RunParameter_RunId",
                table: "RunParameter",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunBountyEntry");

            migrationBuilder.DropTable(
                name: "RunEnemyObservation");

            migrationBuilder.DropTable(
                name: "RunLootEntry");

            migrationBuilder.DropTable(
                name: "RunParameter");

            migrationBuilder.DropTable(
                name: "RunLootCapture");

            migrationBuilder.DropTable(
                name: "Run");
        }
    }
}
