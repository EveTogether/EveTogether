using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddClientServerSessionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServerSessionId",
                table: "ClientServerSession",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServerSessionId",
                table: "ClientServerSession");
        }
    }
}
