using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddRunAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttendanceCount",
                table: "Run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttendanceNotOnRosterCount",
                table: "Run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttendanceSetAtUtc",
                table: "Run",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AttendanceSetByCharacterId",
                table: "Run",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttendanceSource",
                table: "Run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FleetSizeAtStop",
                table: "Run",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InSiteAtCompletion",
                table: "Run",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RunAttendanceEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    CharacterName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsInSite = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsExternal = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    ReasonAmount = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunAttendanceEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunAttendanceEntry_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_RunAttendanceEntry_RunId_CharacterId",
                table: "RunAttendanceEntry",
                columns: new[] { "RunId", "CharacterId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunAttendanceEntry");

            migrationBuilder.DropColumn(
                name: "AttendanceCount",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "AttendanceNotOnRosterCount",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "AttendanceSetAtUtc",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "AttendanceSetByCharacterId",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "AttendanceSource",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "FleetSizeAtStop",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "InSiteAtCompletion",
                table: "Run");
        }
    }
}
