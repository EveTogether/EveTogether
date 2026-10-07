using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddUnrecognisedLootLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnrecognisedLootLine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunLootCaptureId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CharacterId = table.Column<long>(type: "INTEGER", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Quantity = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedTypeId = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedUnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    ResolvedRunLootEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvedRunParameterId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnrecognisedLootLine", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnrecognisedLootLine_RunLootCapture_RunLootCaptureId",
                        column: x => x.RunLootCaptureId,
                        principalTable: "RunLootCapture",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnrecognisedLootLine_RunLootCaptureId",
                table: "UnrecognisedLootLine",
                column: "RunLootCaptureId");

            migrationBuilder.CreateIndex(
                name: "IX_UnrecognisedLootLine_Status",
                table: "UnrecognisedLootLine",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnrecognisedLootLine");
        }
    }
}
