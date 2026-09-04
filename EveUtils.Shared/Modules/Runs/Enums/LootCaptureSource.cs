namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Where a capture's bytes came from. Stored by value, so members are only ever appended.</summary>
public enum LootCaptureSource
{
    /// <summary>The clipboard watch saw an inventory copy go by.</summary>
    Clipboard,

    /// <summary>Typed or pasted into a box in the window, which is a pilot handing it over rather than the app
    /// noticing it.</summary>
    Pasted,

    /// <summary>The loot as the pilot wrote it out himself, replacing the captures it was written from. A different
    /// thing from <see cref="Pasted"/>: that is a cargo hold he handed over, this is his answer to what the run
    /// picked up.</summary>
    Manual
}
