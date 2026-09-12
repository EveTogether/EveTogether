using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddRunMiningEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunMiningEntry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    RunId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OreType = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Units = table.Column<int>(type: "int", nullable: false),
                    CriticalUnits = table.Column<int>(type: "int", nullable: false),
                    ResidueUnits = table.Column<int>(type: "int", nullable: false),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunMiningEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunMiningEntry_Run_RunId",
                        column: x => x.RunId,
                        principalTable: "Run",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_RunMiningEntry_RunId_OreType",
                table: "RunMiningEntry",
                columns: new[] { "RunId", "OreType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunMiningEntry");
        }
    }
}
