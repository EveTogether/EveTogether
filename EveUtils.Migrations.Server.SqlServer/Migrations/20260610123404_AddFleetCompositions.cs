using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetCompositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AssignedCompositionEntryId",
                table: "FleetMember",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedFit_ContentHash",
                table: "FleetMember",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedFit_FitName",
                table: "FleetMember",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedFit_LocalFittingId",
                table: "FleetMember",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedFit_RawJson",
                table: "FleetMember",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedFit_ServerSharedFitId",
                table: "FleetMember",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignedFit_ShipTypeId",
                table: "FleetMember",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FleetCompositionId",
                table: "Fleet",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FleetComposition",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OwnerCharacterId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetComposition", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FleetCompositionRole",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompositionId = table.Column<long>(type: "bigint", nullable: false),
                    RoleName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    GroupMinCount = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetCompositionRole", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetCompositionRole_FleetComposition_CompositionId",
                        column: x => x.CompositionId,
                        principalTable: "FleetComposition",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FleetCompositionEntry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    EntryMinCount = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Fit_ShipTypeId = table.Column<int>(type: "int", nullable: false),
                    Fit_FitName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Fit_RawJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Fit_ContentHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fit_LocalFittingId = table.Column<int>(type: "int", nullable: true),
                    Fit_ServerSharedFitId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetCompositionEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FleetCompositionEntry_FleetCompositionRole_RoleId",
                        column: x => x.RoleId,
                        principalTable: "FleetCompositionRole",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FleetComposition_OwnerCharacterId",
                table: "FleetComposition",
                column: "OwnerCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_FleetCompositionEntry_RoleId",
                table: "FleetCompositionEntry",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_FleetCompositionRole_CompositionId",
                table: "FleetCompositionRole",
                column: "CompositionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FleetCompositionEntry");

            migrationBuilder.DropTable(
                name: "FleetCompositionRole");

            migrationBuilder.DropTable(
                name: "FleetComposition");

            migrationBuilder.DropColumn(
                name: "AssignedCompositionEntryId",
                table: "FleetMember");

            migrationBuilder.DropColumn(
                name: "AssignedFit_ContentHash",
                table: "FleetMember");

            migrationBuilder.DropColumn(
                name: "AssignedFit_FitName",
                table: "FleetMember");

            migrationBuilder.DropColumn(
                name: "AssignedFit_LocalFittingId",
                table: "FleetMember");

            migrationBuilder.DropColumn(
                name: "AssignedFit_RawJson",
                table: "FleetMember");

            migrationBuilder.DropColumn(
                name: "AssignedFit_ServerSharedFitId",
                table: "FleetMember");

            migrationBuilder.DropColumn(
                name: "AssignedFit_ShipTypeId",
                table: "FleetMember");

            migrationBuilder.DropColumn(
                name: "FleetCompositionId",
                table: "Fleet");
        }
    }
}
