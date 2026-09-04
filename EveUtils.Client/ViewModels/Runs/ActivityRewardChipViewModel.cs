using System;
using System.Globalization;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One reward form as its own chip, beside the others and never added into a single figure:
/// <see cref="RunParameterKey"/> only ever grows, and loyalty points and Evermarks have no rate to convert into ISK
/// against (ET-131, design question 3).
///
/// A key this screen carries no label for still gets a chip, named after the key itself and tinted apart. Throwing
/// on it would take the whole row down and a silent <c>default</c> would drop a reward the pilot really earned —
/// both are what ET-161 AC-3 forbids, and both are what a closed <c>switch</c> over a growing enum ends up doing.
/// </summary>
public sealed class ActivityRewardChipViewModel(RunParameterKey key, decimal? amount)
{
    /// <summary>Whether the key came from outside what this screen was taught. Drives the chip's tint, so an
    /// unnamed form is visibly set apart instead of passing for a known one.</summary>
    public bool IsUnknownKind { get; } = !Enum.IsDefined(key);

    public bool IsKnownKind => !IsUnknownKind;

    public string Text { get; } = amount is { } value
        ? $"{_Figure(key, value)} {_Label(key)}"
        : _Label(key);

    private static string _Label(RunParameterKey key) => key switch
    {
        RunParameterKey.Smugglers => "SMUGGLERS",
        RunParameterKey.Civilians => "CIVILIANS",
        RunParameterKey.Isk => "ISK",
        RunParameterKey.BonusIsk => "BONUS",
        RunParameterKey.Bounty => "BOUNTY",
        RunParameterKey.FixedPayout => "PAYOUT",
        RunParameterKey.Escrow => "ESCROW",
        RunParameterKey.LoyaltyPoints => "LP",
        RunParameterKey.Evermarks => "EM",
        RunParameterKey.Item => "ITEMS",
        RunParameterKey.Loot => "LOOT",
        RunParameterKey.Standings => "STANDINGS",
        RunParameterKey.Filament => "FILAMENT",
        RunParameterKey.Escalation => "ESCALATION",
        // A member added after this screen was written still names itself; a value that is in no enum at all says
        // which one it was, because "KIND 41" is answerable and a dropped chip is not.
        _ => Enum.IsDefined(key) ? key.ToString().ToUpperInvariant() : $"KIND {(int)key}"
    };

    /// <summary>ISK-shaped forms are compacted, because their figures are the ones that run to ten digits; a count
    /// is written out, because rounding 1,240 loyalty points to "1.2k" throws away the part that was measured.</summary>
    private static string _Figure(RunParameterKey key, decimal value) => key switch
    {
        RunParameterKey.Isk or RunParameterKey.BonusIsk or RunParameterKey.Bounty
            or RunParameterKey.FixedPayout or RunParameterKey.Escrow => Compact(value),
        _ => value.ToString("#,0.##", CultureInfo.InvariantCulture)
    };

    /// <summary>Signed short form ("84.2M", "-1.2M", "1.2k"), unit-free so the caller supplies the noun.</summary>
    internal static string Compact(decimal value)
    {
        string sign = value < 0 ? "-" : string.Empty;
        decimal size = Math.Abs(value);
        return size switch
        {
            >= 1_000_000_000m => sign + (size / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "B",
            >= 1_000_000m => sign + (size / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "M",
            >= 1_000m => sign + (size / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + "k",
            _ => sign + size.ToString("0.##", CultureInfo.InvariantCulture)
        };
    }
}
