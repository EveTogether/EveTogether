using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddClientFleets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Fleet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Visibility = table.Column<int>(type: "INTEGER", nullable: false),
                    FromTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ToTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatorCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    OfflineBehavior = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Activation = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsClientOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Motd = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsFreeMove = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRegistered = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsVoiceEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EsiFleetId = table.Column<long>(type: "INTEGER", nullable: true),
                    EsiFleetBossId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fleet", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FleetInvite",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FleetId = table.Column<long>(type: "INTEGER", nullable: false),
                    InviterCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    InviteeCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    WingId = table.Column<long>(type: "INTEGER", nullable: true),
                    SquadId = table.Column<long>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetInvite", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetInvite_Fleet_FleetId",
                        column: x => x.FleetId,
                        principalTable: "Fleet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FleetJoinRequest",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FleetId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequesterCharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RespondedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetJoinRequest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetJoinRequest_Fleet_FleetId",
                        column: x => x.FleetId,
                        principalTable: "Fleet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FleetMember",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FleetId = table.Column<long>(type: "INTEGER", nullable: false),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    WingId = table.Column<long>(type: "INTEGER", nullable: false),
                    SquadId = table.Column<long>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    JoinTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsExternal = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShipTypeId = table.Column<int>(type: "INTEGER", nullable: true),
                    SolarSystemId = table.Column<int>(type: "INTEGER", nullable: true),
                    TakesFleetWarp = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetMember", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetMember_Fleet_FleetId",
                        column: x => x.FleetId,
                        principalTable: "Fleet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FleetWing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FleetId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetWing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetWing_Fleet_FleetId",
                        column: x => x.FleetId,
                        principalTable: "Fleet",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FleetSquad",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WingId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetSquad", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetSquad_FleetWing_WingId",
                        column: x => x.WingId,
                        principalTable: "FleetWing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Fleet_CreatorCharacterId",
                table: "Fleet",
                column: "CreatorCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Fleet_Visibility_State",
                table: "Fleet",
                columns: new[] { "Visibility", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_FleetInvite_FleetId",
                table: "FleetInvite",
                column: "FleetId");

            migrationBuilder.CreateIndex(
                name: "IX_FleetInvite_InviteeCharacterId_Status",
                table: "FleetInvite",
                columns: new[] { "InviteeCharacterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FleetJoinRequest_FleetId_Status",
                table: "FleetJoinRequest",
                columns: new[] { "FleetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FleetJoinRequest_RequesterCharacterId_Status",
                table: "FleetJoinRequest",
                columns: new[] { "RequesterCharacterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FleetMember_CharacterId",
                table: "FleetMember",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_FleetMember_FleetId",
                table: "FleetMember",
                column: "FleetId");

            migrationBuilder.CreateIndex(
                name: "IX_FleetMember_FleetId_CharacterId",
                table: "FleetMember",
                columns: new[] { "FleetId", "CharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FleetSquad_WingId",
                table: "FleetSquad",
                column: "WingId");

            migrationBuilder.CreateIndex(
                name: "IX_FleetWing_FleetId",
                table: "FleetWing",
                column: "FleetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FleetInvite");

            migrationBuilder.DropTable(
                name: "FleetJoinRequest");

            migrationBuilder.DropTable(
                name: "FleetMember");

            migrationBuilder.DropTable(
                name: "FleetSquad");

            migrationBuilder.DropTable(
                name: "FleetWing");

            migrationBuilder.DropTable(
                name: "Fleet");
        }
    }
}
