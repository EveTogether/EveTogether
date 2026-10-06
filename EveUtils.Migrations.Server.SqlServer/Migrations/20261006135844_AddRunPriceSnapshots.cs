using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Server.SqlServer.Migrations
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
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PricedAtUtc",
                table: "RunParameter",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPriceIsk",
                table: "RunParameter",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceSource",
                table: "RunMiningEntry",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PricedAtUtc",
                table: "RunMiningEntry",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPriceIsk",
                table: "RunMiningEntry",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceSource",
                table: "RunLootEntry",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PricedAtUtc",
                table: "RunLootEntry",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPriceIsk",
                table: "RunLootEntry",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
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
