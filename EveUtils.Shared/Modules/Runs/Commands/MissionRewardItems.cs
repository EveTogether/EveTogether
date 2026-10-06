using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>An item reward of a mission whose name no SDE type carried when the mission text was read: the reward row
/// has no type id, and its text still says "{count} x {name}" — the form the mission offer writes it in.</summary>
internal static class MissionRewardItems
{
    private const string Separator = " x ";

    public static bool IsUntyped(RunParameter parameter) =>
        parameter.ParameterKey is RunParameterKey.Item && parameter.ItemTypeId is null;

    public static string? NameOf(RunParameter parameter)
    {
        int at = parameter.TypedValue.IndexOf(Separator, StringComparison.Ordinal);
        string name = at < 0 ? string.Empty : parameter.TypedValue[(at + Separator.Length)..].Trim();
        return name.Length == 0 ? null : name;
    }
}
