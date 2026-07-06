using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One launch-tube slot on the Fighter Bay panel (the numbered tubes 1..n, attr 2216). Holds at most one launched
/// <see cref="FighterSquadronViewModel"/>; empty when the slot is open. The panel renders a loaded squadron with its
/// active gauge and "- / +", and an open slot as a "+" that loads a reserve.
/// </summary>
public sealed partial class FighterTubeViewModel : ViewModelBase
{
    public int Index { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private FighterSquadronViewModel? _squadron;

    public bool IsEmpty => Squadron is null;

    public FighterTubeViewModel(int index) => Index = index;
}
