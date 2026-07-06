using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
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
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AssignedFit_FitName",
                table: "FleetMember",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "AssignedFit_LocalFittingId",
                table: "FleetMember",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedFit_RawJson",
                table: "FleetMember",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

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
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OwnerCharacterId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetComposition", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FleetCompositionRole",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CompositionId = table.Column<long>(type: "bigint", nullable: false),
                    RoleName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FleetCompositionEntry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    EntryMinCount = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Fit_ShipTypeId = table.Column<int>(type: "int", nullable: false),
                    Fit_FitName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Fit_RawJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Fit_ContentHash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
