using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EveUtils.Migrations.Server.PostgreSql.Migrations
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EsiCharacterId = table.Column<int>(type: "integer", nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Note = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllowedCharacter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncedCharacter",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EsiCharacterId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RefreshTokenCipher = table.Column<byte[]>(type: "bytea", nullable: false),
                    RefreshTokenNonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    RefreshTokenTag = table.Column<byte[]>(type: "bytea", nullable: false),
                    PairedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncedCharacter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SyncedCharacterId = table.Column<int>(type: "integer", nullable: false),
                    AccessTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeat = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
