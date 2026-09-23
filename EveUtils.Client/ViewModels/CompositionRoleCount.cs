namespace EveUtils.Client.ViewModels;

/// <summary>A doctrine role, how many members fill it, and its group minimum when it sets one.</summary>
public sealed record CompositionRoleCount(string RoleName, int Filled, int? Minimum)
{
    public bool HasMinimum => Minimum is not null;

    public bool IsMet => Minimum is not { } minimum || Filled >= minimum;

    public bool IsShort => !IsMet;

    /// <summary>"DPS 2/2 ✓", "LOGI 1/2", "TRANSPORT 1" — a role without a minimum has nothing to be short of.</summary>
    public string Text => Minimum is { } minimum
        ? $"{RoleName.ToUpperInvariant()} {Filled}/{minimum}{(IsMet ? " ✓" : string.Empty)}"
        : $"{RoleName.ToUpperInvariant()} {Filled}";
}
