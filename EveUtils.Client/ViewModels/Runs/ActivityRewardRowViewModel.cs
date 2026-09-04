using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One reward form on the activity detail, one row per kind and never added up: <see cref="RunParameterKey"/> keeps
/// growing, and loyalty points and Evermarks have no rate to convert into ISK against (ET-160).
///
/// The label is derived from the key rather than mapped, so a key added after this screen was written still reads as
/// itself instead of vanishing or throwing.
/// </summary>
public sealed class ActivityRewardRowViewModel(RunParameterDto parameter)
{
    public RunParameterKey ParameterKey { get; } = parameter.ParameterKey;

    public string Label => string.Concat(ParameterKey.ToString()
        .Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()))
        .ToUpperInvariant();

    /// <summary>The measured amount when there is one, otherwise what the pilot's own line said. Never a zero
    /// standing in for "no figure".</summary>
    public string ValueText { get; } = parameter.Amount is { } amount
        ? amount == Math.Truncate(amount) ? amount.ToString("N0") : amount.ToString("N2")
        : parameter.TypedValue;

    public string? NoteText { get; } = parameter.BonusWindowSeconds is { } seconds
        ? $"within {TimeSpan.FromSeconds(seconds):h\\:mm} h of accepting"
        : parameter.ItemTypeId is { } typeId
            ? $"type {typeId}"
            : null;

    public bool HasNote => NoteText is not null;
}
