namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One room of the saved activity's ENEMIES (ET-240): its window, what was counted in it, and its rows.</summary>
public sealed class ActivityEnemyRoomViewModel(string title, string windowText, int counted,
    IReadOnlyList<ActivityEnemyRowViewModel> rows, RoomSourceViewModel? source = null)
{
    public string Title { get; } = title;

    public string WindowText { get; } = windowText;

    public string CountText { get; } = counted.ToString();

    public IReadOnlyList<ActivityEnemyRowViewModel> Rows { get; } = rows;

    /// <summary>Set when the detector opened this room (ET-368).</summary>
    public RoomSourceViewModel? Source { get; } = source;
}
