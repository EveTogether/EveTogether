using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddServerAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AllowedCharacter",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EsiCharacterId = table.Column<int>(type: "int", nullable: true),
                    CharacterName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllowedCharacter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncedCharacter",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EsiCharacterId = table.Column<int>(type: "int", nullable: false),
                    CharacterName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    RefreshTokenCipher = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    RefreshTokenNonce = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    RefreshTokenTag = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    PairedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncedCharacter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncedCharacterId = table.Column<int>(type: "int", nullable: false),
                    AccessTokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastHeartbeat = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerSession_SyncedCharacter_SyncedCharacterId",
                        column: x => x.SyncedCharacterId,
                        principalTable: "SyncedCharacter",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerSession_AccessTokenHash",
                table: "ServerSession",
                column: "AccessTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_ServerSession_RefreshTokenHash",
                table: "ServerSession",
                column: "RefreshTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_ServerSession_SyncedCharacterId",
                table: "ServerSession",
                column: "SyncedCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncedCharacter_EsiCharacterId",
                table: "SyncedCharacter",
                column: "EsiCharacterId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AllowedCharacter");

            migrationBuilder.DropTable(
                name: "ServerSession");

            migrationBuilder.DropTable(
                name: "SyncedCharacter");
        }
    }
}
