using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddFleet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Fleet",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Visibility = table.Column<int>(type: "int", nullable: false),
                    FromTime = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    ToTime = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true),
                    CreatorCharacterId = table.Column<int>(type: "int", nullable: false),
                    OfflineBehavior = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    Motd = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsFreeMove = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsRegistered = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsVoiceEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    EsiFleetId = table.Column<long>(type: "bigint", nullable: true),
                    EsiFleetBossId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fleet", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FleetInvite",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    FleetId = table.Column<long>(type: "bigint", nullable: false),
                    InviterCharacterId = table.Column<int>(type: "int", nullable: false),
                    InviteeCharacterId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    WingId = table.Column<long>(type: "bigint", nullable: true),
                    SquadId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: true)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FleetMember",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    FleetId = table.Column<long>(type: "bigint", nullable: false),
                    CharacterId = table.Column<int>(type: "int", nullable: false),
                    WingId = table.Column<long>(type: "bigint", nullable: false),
                    SquadId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    JoinTime = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    ShipTypeId = table.Column<int>(type: "int", nullable: true),
                    SolarSystemId = table.Column<int>(type: "int", nullable: true),
                    TakesFleetWarp = table.Column<bool>(type: "tinyint(1)", nullable: false)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FleetWing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    FleetId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FleetSquad",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    WingId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
