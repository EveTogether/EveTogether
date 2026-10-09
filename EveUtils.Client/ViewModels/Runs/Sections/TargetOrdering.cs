using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveUtils.Shared.Modules.Sde.Storage;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>One kind of e-war on a target row. A range is null when the table that named it knows none (ET-369).</summary>
public sealed record TargetEwar(NpcEwarKind Kind, double? RangeMetres, bool IsFromTable)
{
    public string Text => Kind switch
    {
        NpcEwarKind.Scram => "SCRAM",
        NpcEwarKind.Neut => "NEUT",
        NpcEwarKind.Web => "WEB",
        NpcEwarKind.Damp => "DAMP",
        NpcEwarKind.TrackingDisrupt => "TD",
        NpcEwarKind.GuidanceDisrupt => "GD",
        NpcEwarKind.Paint => "PAINT",
        NpcEwarKind.RemoteRepair => "RR",
        _ => "VORTON"
    } + (RangeMetres is { } metres ? $" {(metres / 1000).ToString("0.#", CultureInfo.InvariantCulture)} km" : IsFromTable ? " log" : string.Empty);

    // What the effect does to the pilot, for the chip's outline; damage is the default look.
    public bool IsStops => TargetOrdering.BucketOf(Kind) == 1;

    public bool IsSlows => TargetOrdering.BucketOf(Kind) == 2;

    public bool IsRepairs => TargetOrdering.BucketOf(Kind) == 3;
}

/// <summary>One enemy name seen in a room; the type id is null for a name the SDE has no type for.</summary>
public sealed record TargetSighting(int? Room, string Name, int? TypeId);

/// <summary>What this pilot's combat log shows of one enemy name in one room: the damage dealt, its best 10-second
/// average, and whether the fight moved on without it (a kill, as far as the log can say).</summary>
public sealed record TargetDamage(long Dealt, double PeakDps, bool Gone);

/// <summary>One TARGETS row. <see cref="IsKnown"/> is false for a name neither the SDE nor the table knows.</summary>
public sealed record TargetRow(string Name, IReadOnlyList<TargetEwar> Ewar, double? Ehp, double? Signature, bool IsKnown)
{
    public int Number { get; init; }

    public TargetDamage? Damage { get; init; }

    public bool IsFirst => Number == 1;

    public string SignatureText => Signature is { } signature ? $"sig {signature:0}" : string.Empty;

    // The SDE's EHP leads; without it the damage seen is shown, "observed" once the enemy is gone.
    public string EhpText => Ehp is { } ehp ? _Short(ehp)
        : Damage is { } damage ? $"{_Short(damage.Dealt)}{(damage.Gone ? " observed" : string.Empty)}" : "?";

    public string DamageText => Damage is { } damage ? $"dealt {_Short(damage.Dealt)} · peak {damage.PeakDps:0} dps" : string.Empty;

    private static string _Short(double value) => value switch
    {
        >= 10000 => $"{value / 1000:0}k",
        >= 1000 => $"{value / 1000:0.0}k",
        _ => $"{value:0}"
    };
}

/// <summary>The TARGETS order as a pure function (ET-369): what stops you, then what slows or blinds you, then what
/// repairs others, then damage; the lowest EHP first inside a bucket, a name nobody knows after everything.</summary>
public static class TargetOrdering
{
    public static int BucketOf(NpcEwarKind kind) => kind switch
    {
        NpcEwarKind.Scram or NpcEwarKind.Neut => 1,
        NpcEwarKind.Web or NpcEwarKind.Damp or NpcEwarKind.TrackingDisrupt or NpcEwarKind.GuidanceDisrupt or NpcEwarKind.Paint => 2,
        NpcEwarKind.RemoteRepair => 3,
        _ => 4
    };

    /// <summary>The worst e-war of a row decides its bucket; none at all is damage.</summary>
    public static int Bucket(TargetRow row) => row.Ewar.Select(ewar => BucketOf(ewar.Kind)).DefaultIfEmpty(4).Min();

    /// <summary>A stable sort: rows that tie keep the order they were seen in.</summary>
    public static IReadOnlyList<TargetRow> Order(IEnumerable<TargetRow> rows) =>
    [
        .. rows.OrderBy(row => row.IsKnown ? 0 : 1).ThenBy(Bucket).ThenBy(row => row.Ehp ?? double.MaxValue)
            .Select(row => row with { Ewar =[.. row.Ewar.OrderBy(ewar => BucketOf(ewar.Kind)).ThenBy(ewar => ewar.Kind)] })
            .Select((row, index) => row with { Number = index + 1 })
    ];
}
