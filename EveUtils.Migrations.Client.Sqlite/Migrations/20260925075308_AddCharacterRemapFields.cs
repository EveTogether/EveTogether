using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterRemapFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AccruedRemapCooldownDate",
                table: "CharacterAttributes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BonusRemaps",
                table: "CharacterAttributes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRemapDate",
                table: "CharacterAttributes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccruedRemapCooldownDate",
                table: "CharacterAttributes");

            migrationBuilder.DropColumn(
                name: "BonusRemaps",
                table: "CharacterAttributes");

            migrationBuilder.DropColumn(
                name: "LastRemapDate",
                table: "CharacterAttributes");
        }
    }
}
