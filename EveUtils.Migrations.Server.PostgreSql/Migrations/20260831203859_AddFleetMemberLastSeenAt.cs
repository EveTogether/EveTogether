using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetMemberLastSeenAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "FleetMember",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "FleetMember");
        }
    }
}
