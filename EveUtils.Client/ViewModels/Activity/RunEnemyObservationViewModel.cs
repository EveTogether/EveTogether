using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

public sealed partial class RunEnemyObservationViewModel(int enemyTypeId, string enemyName) : ObservableObject
{
    public int EnemyTypeId { get; } = enemyTypeId;
    public string EnemyName { get; } = enemyName;

    [ObservableProperty]
    private int _count;
}
