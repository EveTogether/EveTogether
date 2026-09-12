using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddActivitySummaryTotalIsk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IskContributions",
                table: "ActivitySummary",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IskSources",
                table: "ActivitySummary",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalIsk",
                table: "ActivitySummary",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IskContributions",
                table: "ActivitySummary");

            migrationBuilder.DropColumn(
                name: "IskSources",
                table: "ActivitySummary");

            migrationBuilder.DropColumn(
                name: "TotalIsk",
                table: "ActivitySummary");
        }
    }
}
