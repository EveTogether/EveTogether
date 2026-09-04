using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddRunAutoSavedStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AutoSavedAtUtc",
                table: "Run",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoSavedAtUtc",
                table: "Run");
        }
    }
}
