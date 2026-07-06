using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
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
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EsiCharacterId = table.Column<int>(type: "int", nullable: true),
                    CharacterName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Note = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllowedCharacter", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SyncedCharacter",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EsiCharacterId = table.Column<int>(type: "int", nullable: false),
                    CharacterName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RefreshTokenCipher = table.Column<byte[]>(type: "longblob", nullable: false),
                    RefreshTokenNonce = table.Column<byte[]>(type: "longblob", nullable: false),
                    RefreshTokenTag = table.Column<byte[]>(type: "longblob", nullable: false),
                    PairedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncedCharacter", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ServerSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    SyncedCharacterId = table.Column<int>(type: "int", nullable: false),
                    AccessTokenHash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RefreshTokenHash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false),
                    LastHeartbeat = table.Column<DateTimeOffset>(type: "datetime(6)", nullable: false)
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
