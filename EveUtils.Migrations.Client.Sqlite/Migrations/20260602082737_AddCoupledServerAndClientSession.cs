using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddCoupledServerAndClientSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientServerSession",
                columns: table => new
                {
                    Address = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CharacterId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CharacterName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SavedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientServerSession", x => new { x.Address, x.CharacterId });
                });

            migrationBuilder.CreateTable(
                name: "CoupledServer",
                columns: table => new
                {
                    Address = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    CertFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoupledServer", x => x.Address);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientServerSession");

            migrationBuilder.DropTable(
                name: "CoupledServer");
        }
    }
}
