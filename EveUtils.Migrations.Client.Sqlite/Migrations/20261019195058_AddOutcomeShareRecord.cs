using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOutcomeShareRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutcomeShareRecord",
                columns: table => new
                {
                    ServerAddress = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ActivityRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CharacterId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    SentRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    SharedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutcomeShareRecord", x => new { x.ServerAddress, x.ActivityRef });
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutcomeShareRecord_ActivityRef",
                table: "OutcomeShareRecord",
                column: "ActivityRef");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutcomeShareRecord");
        }
    }
}
