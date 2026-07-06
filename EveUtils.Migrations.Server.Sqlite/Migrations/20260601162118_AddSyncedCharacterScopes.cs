using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncedCharacterScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GrantedScopes",
                table: "SyncedCharacter",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "GrantedScopesJson",
                table: "SyncedCharacter",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRefreshedAt",
                table: "SyncedCharacter",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrantedScopes",
                table: "SyncedCharacter");

            migrationBuilder.DropColumn(
                name: "GrantedScopesJson",
                table: "SyncedCharacter");

            migrationBuilder.DropColumn(
                name: "LastRefreshedAt",
                table: "SyncedCharacter");
        }
    }
}
