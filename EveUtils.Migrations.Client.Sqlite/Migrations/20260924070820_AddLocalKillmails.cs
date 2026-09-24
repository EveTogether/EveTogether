using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalKillmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocalKillmail",
                columns: table => new
                {
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    KillmailId = table.Column<int>(type: "INTEGER", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    KillmailTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SolarSystemId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsLoss = table.Column<bool>(type: "INTEGER", nullable: false),
                    VictimShipTypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    VictimCharacterId = table.Column<int>(type: "INTEGER", nullable: true),
                    VictimCorporationId = table.Column<int>(type: "INTEGER", nullable: true),
                    VictimAllianceId = table.Column<int>(type: "INTEGER", nullable: true),
                    DamageTaken = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LinkSource = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalKillmail", x => new { x.CharacterId, x.KillmailId });
                    table.ForeignKey(
                        name: "FK_LocalKillmail_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "LocalKillmailAttacker",
                columns: table => new
                {
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    KillmailId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    AttackerCharacterId = table.Column<int>(type: "INTEGER", nullable: true),
                    CorporationId = table.Column<int>(type: "INTEGER", nullable: true),
                    AllianceId = table.Column<int>(type: "INTEGER", nullable: true),
                    FactionId = table.Column<int>(type: "INTEGER", nullable: true),
                    ShipTypeId = table.Column<int>(type: "INTEGER", nullable: true),
                    WeaponTypeId = table.Column<int>(type: "INTEGER", nullable: true),
                    DamageDone = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalBlow = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalKillmailAttacker", x => new { x.CharacterId, x.KillmailId, x.Ordinal });
                    table.ForeignKey(
                        name: "FK_LocalKillmailAttacker_LocalKillmail_CharacterId_KillmailId",
                        columns: x => new { x.CharacterId, x.KillmailId },
                        principalTable: "LocalKillmail",
                        principalColumns: new[] { "CharacterId", "KillmailId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LocalKillmailItem",
                columns: table => new
                {
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    KillmailId = table.Column<int>(type: "INTEGER", nullable: false),
                    Flag = table.Column<int>(type: "INTEGER", nullable: false),
                    TypeId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsNested = table.Column<bool>(type: "INTEGER", nullable: false),
                    QuantityDestroyed = table.Column<long>(type: "INTEGER", nullable: false),
                    QuantityDropped = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalKillmailItem", x => new { x.CharacterId, x.KillmailId, x.Flag, x.TypeId, x.IsNested });
                    table.ForeignKey(
                        name: "FK_LocalKillmailItem_LocalKillmail_CharacterId_KillmailId",
                        columns: x => new { x.CharacterId, x.KillmailId },
                        principalTable: "LocalKillmail",
                        principalColumns: new[] { "CharacterId", "KillmailId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalKillmail_RunId",
                table: "LocalKillmail",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalKillmailAttacker");

            migrationBuilder.DropTable(
                name: "LocalKillmailItem");

            migrationBuilder.DropTable(
                name: "LocalKillmail");
        }
    }
}
