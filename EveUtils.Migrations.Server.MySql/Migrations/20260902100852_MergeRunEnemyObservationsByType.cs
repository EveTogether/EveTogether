using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.MySql.Migrations
{
    /// <inheritdoc />
    public partial class MergeRunEnemyObservationsByType : Migration
    {
        // Dropped and added rather than renamed: EF's scaffold guessed a rename, which would have made every old
        // row's direction (0 or 1) its count. The two columns mean nothing alike. Existing rows keep their name and
        // window and land at count 0; there are none in practice, because STOP threw the list away before a count
        // could be typed and only a count above zero was ever stored (ET-115).
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Direction",
                table: "RunEnemyObservation");

            migrationBuilder.AddColumn<int>(
                name: "Count",
                table: "RunEnemyObservation",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Count",
                table: "RunEnemyObservation");

            migrationBuilder.AddColumn<int>(
                name: "Direction",
                table: "RunEnemyObservation",
                nullable: false,
                defaultValue: 0);
        }
    }
}
