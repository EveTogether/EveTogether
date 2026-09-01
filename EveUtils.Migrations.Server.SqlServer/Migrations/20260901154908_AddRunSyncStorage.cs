using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.SqlServer.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    GroupCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActivityKind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StoppedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SavedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SiteTypeId = table.Column<int>(type: "int", nullable: false),
                    SiteName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    SolarSystemId = table.Column<int>(type: "int", nullable: true),
                    Signature = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    IsPayoutEligible = table.Column<bool>(type: "bit", nullable: false),
                    FitContentHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FitNameSnapshot = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    SyncState = table.Column<int>(type: "int", nullable: false),
                    LastPushedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Run", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunBountyEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
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
                });

            migrationBuilder.CreateTable(
                name: "RunEnemyObservation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnemyTypeId = table.Column<int>(type: "int", nullable: false),
                    EnemyName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "RunLootCapture",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
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
                });

            migrationBuilder.CreateTable(
                name: "RunParameter",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParameterKey = table.Column<int>(type: "int", nullable: false),
                    TypedValue = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "RunLootEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunLootCaptureId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemTypeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
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
                });

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
