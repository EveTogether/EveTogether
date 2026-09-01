using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupDownloadAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupDownload",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AdminUserId = table.Column<int>(type: "int", nullable: false),
                    AdminUsername = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DownloadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AppVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupDownload", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupDownload");
        }
    }
}
