using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddRunParticipationAndFormerGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FormerGroupCode",
                table: "Run",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsParticipant",
                table: "Run",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Rows written before this column existed were flown by their owner. Left at the column
            // default they would each read as "did not fly the site", the opposite of the truth (ET-105).
            migrationBuilder.Sql(@"UPDATE ""Run"" SET ""IsParticipant"" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FormerGroupCode",
                table: "Run");

            migrationBuilder.DropColumn(
                name: "IsParticipant",
                table: "Run");
        }
    }
}
