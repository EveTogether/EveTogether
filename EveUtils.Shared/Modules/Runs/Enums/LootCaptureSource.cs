namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Where a capture's bytes came from. Stored by value, so members are only ever appended.</summary>
public enum LootCaptureSource
{
    /// <summary>The clipboard watch saw an inventory copy go by.</summary>
    Clipboard,

    /// <summary>Typed or pasted into a box in the window, which is a pilot handing it over rather than the app
    /// noticing it.</summary>
    Pasted
}
