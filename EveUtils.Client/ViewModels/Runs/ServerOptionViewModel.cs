using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Runs;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One server on the source chip's list (ET-512 decision 10): its name, what it holds for the activity, and a click that
/// switches the figure to it. The list is the same in the run window's panel and in the overview's detail, so both
/// build their lines from this.
/// </summary>
public sealed partial class ServerOptionViewModel(ExpectedEarnings answer, bool isCurrent, Func<ExpectedEarnings, Task> choose)
    : ViewModelBase
{
    public ExpectedEarnings Answer { get; } = answer;

    public bool IsCurrent { get; } = isCurrent;

    public string NameText { get; } = answer.ServerName ?? answer.ServerAddress ?? "Server";

    public string DetailText { get; } =
        $"{answer.Figures.RunCount.ToString(CultureInfo.InvariantCulture)} runs · {answer.PilotCount.ToString(CultureInfo.InvariantCulture)} pilots";

    [RelayCommand]
    private Task ChooseAsync() => IsCurrent ? Task.CompletedTask : choose(Answer);
}
