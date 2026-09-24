namespace EveUtils.Client.ViewModels.Runs;

public enum StripTone
{
    Empty,
    Neutral,
    Gain,
    Loss
}

/// <summary>How one cell of a strip is shaded: which side of the diverging scale it sits on and how far out (1–4).
/// <see cref="StripTone.Neutral"/> is activity that netted nothing or nothing known — there, but not a gain or a loss.</summary>
public readonly record struct StripLevel(StripTone Tone, int Step)
{
    public static StripLevel Empty { get; } = new(StripTone.Empty, 0);

    public static StripLevel Neutral { get; } = new(StripTone.Neutral, 0);
}
