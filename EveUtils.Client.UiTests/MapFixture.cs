using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The real k-space map of SDE build 3552227 (Fixtures/map-kspace-3552227.json.gz): all 5485 systems with a 2D
/// position, their constellations and regions, the 6989 stargate connections, the region factions, plus two wormhole
/// systems to prove they are left off. Taken straight from CCP's mapSolarSystems/mapConstellations/mapRegions/
/// mapStargates, with coordinates divided by 1e12 (only their ratios matter) and y already negated like the importer
/// does. Real data because the acceptance of ET-392 is stated in real routes: Jita → Amarr, Pochven.
/// </summary>
internal static class MapFixture
{
    public const long BuildNumber = 3552227;

    private static readonly Lazy<(SdeMapSnapshot Map, IReadOnlyDictionary<int, string> Factions)> Data = new(_Read);

    public static FakeSdeAccessor Sde() => new FakeSdeAccessor().WithBuild(BuildNumber).WithMap(Data.Value.Map, Data.Value.Factions);

    public static SdeMapSnapshot Snapshot => Data.Value.Map;

    public static IReadOnlyDictionary<int, string> Factions => Data.Value.Factions;

    private static (SdeMapSnapshot, IReadOnlyDictionary<int, string>) _Read()
    {
        using var gzip = new GZipStream(File.OpenRead(_Path()), CompressionMode.Decompress);
        using JsonDocument document = JsonDocument.Parse(gzip);
        JsonElement root = document.RootElement;

        List<SdeMapSystem> systems = root.GetProperty("systems").EnumerateArray().Select(row => new SdeMapSystem(
            row[0].GetInt32(), row[1].GetString() ?? string.Empty, row[2].GetDouble(), row[3].GetInt32(), row[4].GetInt32(),
            row[5].ValueKind == JsonValueKind.Null ? null : row[5].GetDouble(),
            row[6].ValueKind == JsonValueKind.Null ? null : row[6].GetDouble())).ToList();
        List<SdeMapConstellation> constellations = root.GetProperty("constellations").EnumerateArray().Select(row => new SdeMapConstellation(
            row[0].GetInt32(), row[1].GetString() ?? string.Empty, row[2].GetInt32(),
            row[3].ValueKind == JsonValueKind.Null ? null : row[3].GetInt32())).ToList();
        List<SdeMapRegion> regions = root.GetProperty("regions").EnumerateArray().Select(row => new SdeMapRegion(
            row[0].GetInt32(), row[1].GetString() ?? string.Empty,
            row[2].ValueKind == JsonValueKind.Null ? null : row[2].GetInt32())).ToList();
        List<SdeMapJump> jumps = root.GetProperty("jumps").EnumerateArray()
            .Select(row => new SdeMapJump(row[0].GetInt32(), row[1].GetInt32())).ToList();
        Dictionary<int, string> factions = root.GetProperty("factions").EnumerateArray()
            .ToDictionary(row => row[0].GetInt32(), row => row[1].GetString() ?? string.Empty);

        return (new SdeMapSnapshot(systems, constellations, regions, jumps), factions);
    }

    private static string _Path()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;
        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            "EveUtils.Client.UiTests", "Fixtures", "map-kspace-3552227.json.gz");
    }
}
