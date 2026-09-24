using System.Collections.Generic;
using System.IO;
using System.Linq;
using EveUtils.Client.Composition;
using EveUtils.Shared.Modules.Runs;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// <see cref="HomefrontSignatures"/> is curated by hand from SDE text (ET-348), so it is held against the SDE the
/// player actually has installed, read-only. Skipped where no SDE was ever imported (CI).
/// </summary>
public sealed class HomefrontSignaturesSdeGuardTests
{
    // CCP's "Homefront Operations …" groups, plus 306 (Spawn Container) where Suspicious Signal's hackable arrays sit.
    private static readonly HashSet<long> AllowedGroupIds = [4569, 4570, 4571, 4572, 4573, 4574, 4575, 4576, 4577, 4737, 4771, 306];

    [Fact]
    public void EverySignature_StillExists_InAHomefrontGroup_AndIsNamedByItsSite()
    {
        string path = Path.Combine(ClientDataLocation.DefaultRoot, "sde", "sde.sqlite");
        Assert.SkipUnless(File.Exists(path), "No SDE has been imported on this machine.");
        using var sde = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        sde.Open();

        Assert.Empty(_Violations(sde, HomefrontSignatures.DungeonIdByTypeId));
        Assert.Equal(["999999999 is not in the SDE"],
            _Violations(sde, new Dictionary<int, int>(HomefrontSignatures.DungeonIdByTypeId) { [999999999] = 10347 }));
    }

    // A site's gameplay text names its hallmark in full (plurals included) or, for three Suspicious Signal
    // structures, by its last word alone ("make the Broadcaster vulnerable").
    private static List<string> _Violations(SqliteConnection sde, IReadOnlyDictionary<int, int> table)
    {
        List<string> violations = [];
        foreach ((int typeId, int dungeonId) in table)
        {
            using SqliteCommand command = sde.CreateCommand();
            command.CommandText =
                """
                SELECT t.groupId, t.nameEn, s.gameplayDescription
                FROM Type t LEFT JOIN Site s ON s.dungeonId = $dungeon AND s.archetypeId = 70
                WHERE t.typeId = $type;
                """;
            command.Parameters.AddWithValue("$type", typeId);
            command.Parameters.AddWithValue("$dungeon", dungeonId);
            using SqliteDataReader row = command.ExecuteReader();
            if (!row.Read())
                violations.Add($"{typeId} is not in the SDE");
            else if (!AllowedGroupIds.Contains(row.GetInt64(0)))
                violations.Add($"{typeId} {row.GetString(1)} is in group {row.GetInt64(0)}");
            else if (row.IsDBNull(2) || !_Names(row.GetString(2), row.GetString(1)))
                violations.Add($"{typeId} {row.GetString(1)} is not named by homefront {dungeonId}");
        }

        return violations;
    }

    private static bool _Names(string gameplayDescription, string typeName) =>
        gameplayDescription.Contains(typeName) || gameplayDescription.Contains($"the {typeName.Split(' ').Last()}");
}
