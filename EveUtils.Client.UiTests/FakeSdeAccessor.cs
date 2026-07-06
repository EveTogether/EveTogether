using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Storage;

namespace EveUtils.Client.UiTests;

/// <summary>
/// In-memory <see cref="ISdeAccessor"/> for fast, deterministic fit-parser tests — a handful of known types with
/// their slot + category, no real SDE file. Categories: 6 Ship, 7 Module, 8 Charge, 18 Drone (the importer keys
/// drone-vs-cargo on 18).
/// </summary>
public sealed class FakeSdeAccessor : ISdeAccessor
{
    private sealed record Entry(int TypeId, string Name, int GroupId, SdeSlotType Slot, bool IsTurret, double Volume);

    private readonly Dictionary<int, Entry> _types = new();
    private readonly Dictionary<int, int> _groupCategory = new();
    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<SdeDogmaAttribute>> _attrs = new();

    public bool IsAvailable { get; private set; } = true;
    public SdeVersion? Version => new(1, DateTimeOffset.UnixEpoch);

    public FakeSdeAccessor Add(int typeId, string name, int groupId, int categoryId, SdeSlotType slot = SdeSlotType.None, bool isTurret = false, double volume = 0)
    {
        _types[typeId] = new Entry(typeId, name, groupId, slot, isTurret, volume);
        _groupCategory[groupId] = categoryId;
        _byName[name] = typeId;
        return this;
    }

    public FakeSdeAccessor Attr(int typeId, int attributeId, double value)
    {
        if (!_attrs.TryGetValue(typeId, out var list))
            _attrs[typeId] = list = [];
        list.Add(new SdeDogmaAttribute(attributeId, value));
        return this;
    }

    public FakeSdeAccessor Offline()
    {
        IsAvailable = false;
        return this;
    }

    public bool TryGetTypeName(int typeId, out string name)
    {
        if (_types.TryGetValue(typeId, out var entry)) { name = entry.Name; return true; }
        name = string.Empty;
        return false;
    }

    public bool TryGetTypeId(string name, out int typeId) => _byName.TryGetValue(name.Trim(), out typeId);

    public SdeType? GetType(int typeId) =>
        _types.TryGetValue(typeId, out var e) ? new SdeType(e.TypeId, e.GroupId, e.Name, true, 0, e.Volume, 0, null) : null;

    public IReadOnlyList<SdeDogmaAttribute> GetDogmaAttributes(int typeId) =>
        _attrs.TryGetValue(typeId, out var list) ? list : [];

    public IReadOnlyList<SdeChargeType> GetChargeTypesInGroup(int groupId) =>
        _types.Values.Where(e => e.GroupId == groupId)
            .Select(e => new SdeChargeType(e.TypeId, e.Name,
                _attrs.TryGetValue(e.TypeId, out var a) ? a.FirstOrDefault(x => x.AttributeId == 128)?.Value : null))
            .ToList();

    public SdeFitRequirement? GetFitRequirement(int typeId) =>
        _types.TryGetValue(typeId, out var e) && e.Slot != SdeSlotType.None
            ? new SdeFitRequirement(e.Slot, 1, false, e.IsTurret)
            : null;

    public SdeSlotType GetSlotType(int typeId) => _types.TryGetValue(typeId, out var e) ? e.Slot : SdeSlotType.None;

    public IReadOnlyList<SdeNamedType> GetBoosterTypes() =>
        _types.Values
            .Where(e => _attrs.TryGetValue(e.TypeId, out var a) && a.Any(x => x.AttributeId == 1087))
            .Select(e => new SdeNamedType(e.TypeId, e.Name))
            .OrderBy(t => t.Name)
            .ToList();

    public IReadOnlyList<SdeNamedType> GetFighterTypes() =>
        _types.Values
            .Where(e => _groupCategory.TryGetValue(e.GroupId, out var cat) && cat == 87)
            .Select(e => new SdeNamedType(e.TypeId, e.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<SdeEnvironmentBeacon> GetEnvironmentBeacons() =>
        _types.Values
            .Where(e => e.GroupId == 920)
            .Select(e => EnvironmentBeaconClassifier.Classify(e.TypeId, e.Name))
            .Where(beacon => beacon is not null)
            .Select(beacon => beacon!)
            .OrderBy(beacon => beacon.SortOrder)
            .ToList();

    public SdeGroup? GetGroup(int groupId) =>
        _groupCategory.TryGetValue(groupId, out var cat) ? new SdeGroup(groupId, cat, "", true) : null;

    public IReadOnlyList<SdeGroup> GetGroupsByCategory(int categoryId) =>
        _groupCategory.Where(kv => kv.Value == categoryId)
            .Select(kv => new SdeGroup(kv.Key, categoryId, "", true)).ToList();

    public SdeCategory? GetCategory(int categoryId) => new(categoryId, "", true);

    public IReadOnlyList<NpcEnemy> SearchNpcEnemies(string q) => [];

    public DamageProfile? GetNpcDamageProfile(int typeId) => null;

    public void Close() { }
    public void Reopen() { }

    /// <summary>A small fixture: Rifter hull + a low/med/high module, a turret charge, a drone and a cargo item.</summary>
    public static FakeSdeAccessor WithSampleFit() => new FakeSdeAccessor()
        .Add(587, "Rifter", 25, 6)                                   // ship
        .Add(2048, "Damage Control II", 60, 7, SdeSlotType.Low)
        .Add(438, "1MN Afterburner II", 46, 7, SdeSlotType.Medium)
        .Add(2889, "200mm AutoCannon II", 55, 7, SdeSlotType.High, isTurret: true)
        .Add(12608, "EMP S", 83, 8)                                  // charge
        .Add(2456, "Hobgoblin II", 100, 18)                          // drone
        .Add(28668, "Nanite Repair Paste", 285, 7);                  // cargo (not drone)
}
