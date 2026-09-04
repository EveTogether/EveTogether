namespace EveUtils.Client.ViewModels.Fleets;

/// <summary>The ink of a small bordered chip: the theme's chip classes, named for what they say rather than how.</summary>
public enum FleetChipTone
{
    Dim,
    Ok,
    Warn,
    Good,
    Value,
}

/// <summary>One tally or note drawn as a chip — "43 linked", "active since 20:14", "2 share nothing" — amber when it
/// names something that asks for attention, quiet otherwise.</summary>
public sealed record FleetCountChipViewModel(string Text, FleetChipTone Tone = FleetChipTone.Dim)
{
    public bool IsDim => Tone == FleetChipTone.Dim;
    public bool IsOk => Tone == FleetChipTone.Ok;
    public bool IsWarning => Tone == FleetChipTone.Warn;
    public bool IsGood => Tone == FleetChipTone.Good;
    public bool IsValue => Tone == FleetChipTone.Value;
}
