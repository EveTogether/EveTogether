using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddRunPriceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PriceSource",
                table: "RunParameter",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PricedAtUtc",
                table: "RunParameter",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPriceIsk",
                table: "RunParameter",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceSource",
                table: "RunMiningEntry",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PricedAtUtc",
                table: "RunMiningEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPriceIsk",
                table: "RunMiningEntry",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceSource",
                table: "RunLootEntry",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PricedAtUtc",
                table: "RunLootEntry",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPriceIsk",
                table: "RunLootEntry",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: true);

            // ET-463: the price at capture is not known any more, so every stored line is fixed at today's cache price,
            // marked Migrated (3). A line the cache has no price for stays open and is filled once later.
            migrationBuilder.Sql("""
                UPDATE "RunLootEntry"
                SET "UnitPriceIsk" = printf('%.2f', (SELECT "p"."AveragePrice" FROM "LocalMarketPrice" AS "p" WHERE "p"."TypeId" = "RunLootEntry"."ItemTypeId")),
                    "PricedAtUtc" = strftime('%Y-%m-%d %H:%M:%S', 'now'),
                    "PriceSource" = 3
                WHERE EXISTS (SELECT 1 FROM "LocalMarketPrice" AS "p" WHERE "p"."TypeId" = "RunLootEntry"."ItemTypeId");
                """);
            // The filament's type row (RunParameterKey.AbyssalFilamentTypeId = 22) holds the type id as text.
            migrationBuilder.Sql("""
                UPDATE "RunParameter"
                SET "UnitPriceIsk" = printf('%.2f', (SELECT "p"."AveragePrice" FROM "LocalMarketPrice" AS "p" WHERE "p"."TypeId" = CAST("RunParameter"."TypedValue" AS INTEGER))),
                    "PricedAtUtc" = strftime('%Y-%m-%d %H:%M:%S', 'now'),
                    "PriceSource" = 3
                WHERE "ParameterKey" = 22
                  AND EXISTS (SELECT 1 FROM "LocalMarketPrice" AS "p" WHERE "p"."TypeId" = CAST("RunParameter"."TypedValue" AS INTEGER));
                """);
            // An ore is priced by its SDE type, which SQL cannot reach: marked Migrated without a price, and priced by
            // FillRunPriceSnapshotsCommand at the next start, as a migration rather than as a correction.
            migrationBuilder.Sql("""
                UPDATE "RunMiningEntry" SET "PriceSource" = 3;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceSource",
                table: "RunParameter");

            migrationBuilder.DropColumn(
                name: "PricedAtUtc",
                table: "RunParameter");

            migrationBuilder.DropColumn(
                name: "UnitPriceIsk",
                table: "RunParameter");

            migrationBuilder.DropColumn(
                name: "PriceSource",
                table: "RunMiningEntry");

            migrationBuilder.DropColumn(
                name: "PricedAtUtc",
                table: "RunMiningEntry");

            migrationBuilder.DropColumn(
                name: "UnitPriceIsk",
                table: "RunMiningEntry");

            migrationBuilder.DropColumn(
                name: "PriceSource",
                table: "RunLootEntry");

            migrationBuilder.DropColumn(
                name: "PricedAtUtc",
                table: "RunLootEntry");

            migrationBuilder.DropColumn(
                name: "UnitPriceIsk",
                table: "RunLootEntry");
        }
    }
}
