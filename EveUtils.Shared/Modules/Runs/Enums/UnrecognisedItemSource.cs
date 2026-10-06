namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Where an item name that no SDE type carries was entered. Every input that reads item names records here,
/// so one log answers "what is still unknown" whatever the way in was.</summary>
public enum UnrecognisedItemSource
{
    /// <summary>An inventory copy the clipboard watch picked up.</summary>
    ClipboardCapture,

    /// <summary>Typed or pasted into a box of the run window: a cargo hold, a hand-written loot list, a spent list.</summary>
    RunWindowEntry,

    /// <summary>The appraisal tool. Belongs to no run, so it is only ever logged, never moved into loot.</summary>
    Appraisal,

    /// <summary>The item a mission pays out, read off the mission text. It is not kept as a line while it is open — the
    /// reward row on the run is the open record — and only gets one when it is recognised, as the history of it.</summary>
    MissionReward
}
