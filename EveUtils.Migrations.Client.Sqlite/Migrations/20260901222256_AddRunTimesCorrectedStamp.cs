using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddRunTimesCorrectedStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TimesCorrectedAtUtc",
                table: "Run",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimesCorrectedAtUtc",
                table: "Run");
        }
    }
}
