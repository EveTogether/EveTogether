using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EveUtils.Migrations.Client.Sqlite.Migrations
{
    /// <summary>
    /// I7 (ET-274): at most one live run per character per group. Every multi-toon homefront started on the ET-271
    /// client doubled its siblings (HF-DYB4, HF-FMEC: nine runs for five characters), so the duplicates already stored
    /// are folded first, by the rules OneRunPerCharacter.Merge applies at runtime: the first-made run of each character
    /// stays, every fact only a later copy has moves onto it — never one it already has — and the copy is deleted the
    /// way a delete is published. The startup rebuild (IskContributors revision 3) then adds each activity up again.
    /// </summary>
    public partial class EnforceOneRunPerCharacterInGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Run_GroupCode_CharacterId",
                table: "Run");

            // 1. Which run of each doubled (group, character) stays: the first made — ids are time-ordered UUIDv7.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "ET274_Fold" ("LoserId" TEXT NOT NULL PRIMARY KEY, "SurvivorId" TEXT NOT NULL);
                """);
            migrationBuilder.Sql("""
                INSERT INTO "ET274_Fold" ("LoserId", "SurvivorId")
                SELECT "r"."Id", "s"."SurvivorId"
                FROM "Run" AS "r"
                JOIN (SELECT "GroupCode", "CharacterId", MIN("Id") AS "SurvivorId" FROM "Run"
                      WHERE "GroupCode" IS NOT NULL AND "DeletedAtUtc" IS NULL
                      GROUP BY "GroupCode", "CharacterId" HAVING COUNT(*) > 1) AS "s"
                  ON "s"."GroupCode" = "r"."GroupCode" AND "s"."CharacterId" = "r"."CharacterId"
                WHERE "r"."DeletedAtUtc" IS NULL AND "r"."Id" <> "s"."SurvivorId";
                """);

            // 2. Bounty: a line moves unless the survivor already has it (same moment, same amount), and of two copies
            //    of one line only one moves.
            migrationBuilder.Sql("""
                UPDATE "RunBountyEntry"
                SET "RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunBountyEntry"."RunId")
                WHERE "RunId" IN (SELECT "LoserId" FROM "ET274_Fold")
                  AND NOT EXISTS (SELECT 1 FROM "RunBountyEntry" AS "k"
                                  WHERE "k"."RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunBountyEntry"."RunId")
                                    AND "k"."OccurredAtUtc" = "RunBountyEntry"."OccurredAtUtc" AND "k"."Isk" = "RunBountyEntry"."Isk")
                  AND "Id" = (SELECT MIN("b"."Id") FROM "RunBountyEntry" AS "b" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "b"."RunId"
                              WHERE "f"."SurvivorId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunBountyEntry"."RunId")
                                AND "b"."OccurredAtUtc" = "RunBountyEntry"."OccurredAtUtc" AND "b"."Isk" = "RunBountyEntry"."Isk");
                """);

            // 3. Loot: a copy moves with its lines unless the survivor holds the same content.
            migrationBuilder.Sql("""
                UPDATE "RunLootCapture"
                SET "RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunLootCapture"."RunId")
                WHERE "RunId" IN (SELECT "LoserId" FROM "ET274_Fold")
                  AND ("ContentHash" IS NULL OR (
                       NOT EXISTS (SELECT 1 FROM "RunLootCapture" AS "k"
                                   WHERE "k"."RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunLootCapture"."RunId")
                                     AND "k"."ContentHash" = "RunLootCapture"."ContentHash")
                       AND "Id" = (SELECT MIN("c"."Id") FROM "RunLootCapture" AS "c" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "c"."RunId"
                                   WHERE "f"."SurvivorId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunLootCapture"."RunId")
                                     AND "c"."ContentHash" = "RunLootCapture"."ContentHash")));
                """);

            // 4. Parameters: one per key and item.
            migrationBuilder.Sql("""
                UPDATE "RunParameter"
                SET "RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunParameter"."RunId")
                WHERE "RunId" IN (SELECT "LoserId" FROM "ET274_Fold")
                  AND NOT EXISTS (SELECT 1 FROM "RunParameter" AS "k"
                                  WHERE "k"."RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunParameter"."RunId")
                                    AND "k"."ParameterKey" = "RunParameter"."ParameterKey" AND "k"."ItemTypeId" IS "RunParameter"."ItemTypeId")
                  AND "Id" = (SELECT MIN("p"."Id") FROM "RunParameter" AS "p" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "p"."RunId"
                              WHERE "f"."SurvivorId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunParameter"."RunId")
                                AND "p"."ParameterKey" = "RunParameter"."ParameterKey" AND "p"."ItemTypeId" IS "RunParameter"."ItemTypeId");
                """);

            // 5. Enemies: an enemy the survivor also saw keeps the larger typed count and the widest window; one it
            //    did not moves.
            migrationBuilder.Sql("""
                UPDATE "RunEnemyObservation"
                SET ("Count", "FirstObservedAtUtc", "LastObservedAtUtc") = (
                    SELECT MAX("RunEnemyObservation"."Count", MAX("e"."Count")),
                           MIN("RunEnemyObservation"."FirstObservedAtUtc", MIN("e"."FirstObservedAtUtc")),
                           MAX("RunEnemyObservation"."LastObservedAtUtc", MAX("e"."LastObservedAtUtc"))
                    FROM "RunEnemyObservation" AS "e" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "e"."RunId"
                    WHERE "f"."SurvivorId" = "RunEnemyObservation"."RunId"
                      AND "e"."EnemyTypeId" = "RunEnemyObservation"."EnemyTypeId" AND "e"."EnemyName" = "RunEnemyObservation"."EnemyName")
                WHERE "RunId" IN (SELECT "SurvivorId" FROM "ET274_Fold")
                  AND EXISTS (SELECT 1 FROM "RunEnemyObservation" AS "e" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "e"."RunId"
                              WHERE "f"."SurvivorId" = "RunEnemyObservation"."RunId"
                                AND "e"."EnemyTypeId" = "RunEnemyObservation"."EnemyTypeId" AND "e"."EnemyName" = "RunEnemyObservation"."EnemyName");
                """);
            migrationBuilder.Sql("""
                UPDATE "RunEnemyObservation"
                SET "RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunEnemyObservation"."RunId")
                WHERE "RunId" IN (SELECT "LoserId" FROM "ET274_Fold")
                  AND NOT EXISTS (SELECT 1 FROM "RunEnemyObservation" AS "k"
                                  WHERE "k"."RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunEnemyObservation"."RunId")
                                    AND "k"."EnemyTypeId" = "RunEnemyObservation"."EnemyTypeId" AND "k"."EnemyName" = "RunEnemyObservation"."EnemyName")
                  AND "Id" = (SELECT MIN("e"."Id") FROM "RunEnemyObservation" AS "e" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "e"."RunId"
                              WHERE "f"."SurvivorId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunEnemyObservation"."RunId")
                                AND "e"."EnemyTypeId" = "RunEnemyObservation"."EnemyTypeId" AND "e"."EnemyName" = "RunEnemyObservation"."EnemyName");
                """);

            // 6. Mining: one row per ore; the fuller copy of an ore both hold wins, one only a copy holds moves.
            migrationBuilder.Sql("""
                UPDATE "RunMiningEntry"
                SET ("Units", "CriticalUnits", "ResidueUnits", "FirstObservedAtUtc", "LastObservedAtUtc") = (
                    SELECT "m"."Units", "m"."CriticalUnits", "m"."ResidueUnits", "m"."FirstObservedAtUtc", "m"."LastObservedAtUtc"
                    FROM "RunMiningEntry" AS "m" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "m"."RunId"
                    WHERE "f"."SurvivorId" = "RunMiningEntry"."RunId" AND "m"."OreType" = "RunMiningEntry"."OreType"
                    ORDER BY "m"."Units" DESC LIMIT 1)
                WHERE "RunId" IN (SELECT "SurvivorId" FROM "ET274_Fold")
                  AND EXISTS (SELECT 1 FROM "RunMiningEntry" AS "m" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "m"."RunId"
                              WHERE "f"."SurvivorId" = "RunMiningEntry"."RunId" AND "m"."OreType" = "RunMiningEntry"."OreType"
                                AND "m"."Units" > "RunMiningEntry"."Units");
                """);
            migrationBuilder.Sql("""
                UPDATE "RunMiningEntry"
                SET "RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunMiningEntry"."RunId")
                WHERE "RunId" IN (SELECT "LoserId" FROM "ET274_Fold")
                  AND NOT EXISTS (SELECT 1 FROM "RunMiningEntry" AS "k"
                                  WHERE "k"."RunId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunMiningEntry"."RunId")
                                    AND "k"."OreType" = "RunMiningEntry"."OreType")
                  AND "Id" = (SELECT MIN("m"."Id") FROM "RunMiningEntry" AS "m" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "m"."RunId"
                              WHERE "f"."SurvivorId" = (SELECT "SurvivorId" FROM "ET274_Fold" WHERE "LoserId" = "RunMiningEntry"."RunId")
                                AND "m"."OreType" = "RunMiningEntry"."OreType");
                """);

            // 7. The outcome: the newest list that says one, among every copy — an outcome is never erased (I4).
            //    Before the list itself moves, while each copy still carries its own time.
            migrationBuilder.Sql("""
                UPDATE "Run"
                SET ("HomefrontOutcome", "HomefrontCompletedWaveCount", "HomefrontOutcomeFromGameLog") = (
                    SELECT "o"."HomefrontOutcome", "o"."HomefrontCompletedWaveCount", "o"."HomefrontOutcomeFromGameLog"
                    FROM "Run" AS "o"
                    WHERE ("o"."Id" = "Run"."Id" OR "o"."Id" IN (SELECT "LoserId" FROM "ET274_Fold" WHERE "SurvivorId" = "Run"."Id"))
                      AND ("o"."HomefrontOutcome" IS NOT NULL OR "o"."HomefrontCompletedWaveCount" IS NOT NULL)
                    ORDER BY "o"."AttendanceSetAtUtc" IS NULL, "o"."AttendanceSetAtUtc" DESC LIMIT 1)
                WHERE "Id" IN (SELECT "SurvivorId" FROM "ET274_Fold")
                  AND EXISTS (SELECT 1 FROM "Run" AS "o"
                              WHERE ("o"."Id" = "Run"."Id" OR "o"."Id" IN (SELECT "LoserId" FROM "ET274_Fold" WHERE "SurvivorId" = "Run"."Id"))
                                AND ("o"."HomefrontOutcome" IS NOT NULL OR "o"."HomefrontCompletedWaveCount" IS NOT NULL));
                """);

            // 8. Attendance: a copy's list replaces the survivor's only when it is newer.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "ET274_List" ("SurvivorId" TEXT NOT NULL PRIMARY KEY, "SourceId" TEXT NOT NULL);
                """);
            migrationBuilder.Sql("""
                INSERT INTO "ET274_List" ("SurvivorId", "SourceId")
                SELECT "s"."Id",
                       (SELECT "l"."Id" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id"
                        WHERE "f"."SurvivorId" = "s"."Id" AND "l"."AttendanceSetAtUtc" IS NOT NULL
                        ORDER BY "l"."AttendanceSetAtUtc" DESC, "l"."Id" LIMIT 1)
                FROM "Run" AS "s"
                WHERE "s"."Id" IN (SELECT "SurvivorId" FROM "ET274_Fold")
                  AND EXISTS (SELECT 1 FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id"
                              WHERE "f"."SurvivorId" = "s"."Id" AND "l"."AttendanceSetAtUtc" IS NOT NULL
                                AND ("s"."AttendanceSetAtUtc" IS NULL OR "l"."AttendanceSetAtUtc" > "s"."AttendanceSetAtUtc"));
                """);
            migrationBuilder.Sql("""
                DELETE FROM "RunAttendanceEntry" WHERE "RunId" IN (SELECT "SurvivorId" FROM "ET274_List");
                """);
            migrationBuilder.Sql("""
                UPDATE "RunAttendanceEntry"
                SET "RunId" = (SELECT "SurvivorId" FROM "ET274_List" WHERE "SourceId" = "RunAttendanceEntry"."RunId")
                WHERE "RunId" IN (SELECT "SourceId" FROM "ET274_List");
                """);
            migrationBuilder.Sql("""
                UPDATE "Run"
                SET ("InSiteAtCompletion", "AttendanceCount", "AttendanceNotOnRosterCount", "AttendanceSource",
                     "AttendanceSetByCharacterId", "AttendanceSetAtUtc", "HomefrontPayoutTableVersion") = (
                    SELECT "l"."InSiteAtCompletion", "l"."AttendanceCount", "l"."AttendanceNotOnRosterCount", "l"."AttendanceSource",
                           "l"."AttendanceSetByCharacterId", "l"."AttendanceSetAtUtc",
                           COALESCE("l"."HomefrontPayoutTableVersion", "Run"."HomefrontPayoutTableVersion")
                    FROM "Run" AS "l" JOIN "ET274_List" AS "t" ON "t"."SourceId" = "l"."Id"
                    WHERE "t"."SurvivorId" = "Run"."Id")
                WHERE "Id" IN (SELECT "SurvivorId" FROM "ET274_List");
                """);

            // 9. What a copy knows and the survivor does not, and the further state of the two.
            migrationBuilder.Sql("""
                UPDATE "Run"
                SET "FitContentHash" = COALESCE("FitContentHash", (SELECT "l"."FitContentHash" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."FitContentHash" IS NOT NULL ORDER BY "l"."Id" LIMIT 1)),
                    "FitNameSnapshot" = COALESCE("FitNameSnapshot", (SELECT "l"."FitNameSnapshot" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."FitNameSnapshot" IS NOT NULL ORDER BY "l"."Id" LIMIT 1)),
                    "CharacterNameSnapshot" = COALESCE("CharacterNameSnapshot", (SELECT "l"."CharacterNameSnapshot" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."CharacterNameSnapshot" IS NOT NULL ORDER BY "l"."Id" LIMIT 1)),
                    "SignatureGroupSnapshot" = COALESCE("SignatureGroupSnapshot", (SELECT "l"."SignatureGroupSnapshot" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."SignatureGroupSnapshot" IS NOT NULL ORDER BY "l"."Id" LIMIT 1)),
                    "SolarSystemId" = COALESCE("SolarSystemId", (SELECT "l"."SolarSystemId" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."SolarSystemId" IS NOT NULL ORDER BY "l"."Id" LIMIT 1)),
                    "AgentId" = COALESCE("AgentId", (SELECT "l"."AgentId" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."AgentId" IS NOT NULL ORDER BY "l"."Id" LIMIT 1)),
                    "MissionLevel" = COALESCE("MissionLevel", (SELECT "l"."MissionLevel" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."MissionLevel" IS NOT NULL ORDER BY "l"."Id" LIMIT 1)),
                    "LootStrategy" = COALESCE("LootStrategy", (SELECT "l"."LootStrategy" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."LootStrategy" IS NOT NULL ORDER BY "l"."Id" LIMIT 1)),
                    "FleetSizeAtStop" = COALESCE("FleetSizeAtStop", (SELECT "l"."FleetSizeAtStop" FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id" WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."FleetSizeAtStop" IS NOT NULL ORDER BY "l"."Id" LIMIT 1))
                WHERE "Id" IN (SELECT "SurvivorId" FROM "ET274_Fold");
                """);
            migrationBuilder.Sql("""
                UPDATE "Run"
                SET ("State", "StoppedAtUtc", "SavedAtUtc", "AutoSavedAtUtc") = (
                    SELECT "l"."State", COALESCE("Run"."StoppedAtUtc", "l"."StoppedAtUtc"),
                           COALESCE("Run"."SavedAtUtc", "l"."SavedAtUtc"), COALESCE("Run"."AutoSavedAtUtc", "l"."AutoSavedAtUtc")
                    FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id"
                    WHERE "f"."SurvivorId" = "Run"."Id" ORDER BY "l"."State" DESC LIMIT 1)
                WHERE "Id" IN (SELECT "SurvivorId" FROM "ET274_Fold")
                  AND EXISTS (SELECT 1 FROM "Run" AS "l" JOIN "ET274_Fold" AS "f" ON "f"."LoserId" = "l"."Id"
                              WHERE "f"."SurvivorId" = "Run"."Id" AND "l"."State" > "Run"."State");
                """);

            // 10. The survivor changed (Synced → Outdated, ET-215); the copy is deleted as DeleteRunCommand deletes —
            //     a published copy goes back up as a deletion (Pending), a local one stays local.
            migrationBuilder.Sql("""
                UPDATE "Run" SET "Revision" = "Revision" + 1, "SyncState" = CASE WHEN "SyncState" = 2 THEN 3 ELSE "SyncState" END
                WHERE "Id" IN (SELECT "SurvivorId" FROM "ET274_Fold");
                """);
            migrationBuilder.Sql("""
                UPDATE "Run"
                SET "DeletedAtUtc" = strftime('%Y-%m-%d %H:%M:%f', 'now'), "Revision" = "Revision" + 1,
                    "SyncState" = CASE WHEN "SyncState" = 0 THEN 0 ELSE 1 END
                WHERE "Id" IN (SELECT "LoserId" FROM "ET274_Fold");
                """);
            migrationBuilder.Sql("""DROP TABLE "ET274_List";""");
            migrationBuilder.Sql("""DROP TABLE "ET274_Fold";""");

            migrationBuilder.CreateIndex(
                name: "IX_Run_GroupCode_CharacterId",
                table: "Run",
                columns: new[] { "GroupCode", "CharacterId" },
                unique: true,
                filter: "\"GroupCode\" IS NOT NULL AND \"DeletedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Run_GroupCode_CharacterId",
                table: "Run");

            migrationBuilder.CreateIndex(
                name: "IX_Run_GroupCode_CharacterId",
                table: "Run",
                columns: new[] { "GroupCode", "CharacterId" });
        }
    }
}
