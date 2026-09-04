namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>What a capture is to the run, which is a different question from where its bytes came from
/// (<see cref="LootCaptureSource"/>): a cargo hold pasted before the run and one copied during it can both arrive
/// from the clipboard. Stored by value, so members are only ever appended.</summary>
public enum LootCaptureRole
{
    /// <summary>A moment during the run. On its own it is the loot; alongside a <see cref="CargoBefore"/> it counts
    /// towards nothing, because the difference between two cargo holds already covers it.</summary>
    Snapshot,

    /// <summary>The cargo hold the run started from — the zero point, never loot itself.</summary>
    CargoBefore,

    /// <summary>The cargo hold the run ended on. Left unset, the last capture is it.</summary>
    CargoAfter
}
