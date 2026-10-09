namespace EveUtils.Client.ViewModels.Activity;

/// <summary>One room of the run window's ENEMIES list (ET-240): its header and the rows seen in it.</summary>
public sealed class RunEnemyRoomViewModel(int number, string windowText, bool isUndoShown,
    IReadOnlyList<RunEnemyObservationViewModel> observations, Runs.RoomSourceViewModel? source = null)
{
    public int Number { get; } = number;

    public string Title { get; } = $"ROOM {number}";

    public string WindowText { get; } = windowText;

    /// <summary>Only the last room can be taken back — a boundary in the middle is never moved.</summary>
    public bool IsUndoShown { get; } = isUndoShown;

    public IReadOnlyList<RunEnemyObservationViewModel> Observations { get; } = observations;

    /// <summary>Set when the detector opened this room (ET-368).</summary>
    public Runs.RoomSourceViewModel? Source { get; } = source;
}
