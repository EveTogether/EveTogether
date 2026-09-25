using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Skills;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One doctrine skill minimum under a fit entry in the composition editor ("DOCTRINE MINIMUM"). <see cref="FitLevel"/>
/// is what the fit itself already requires of the skill (0 = not at all), so the hint can say when the minimum
/// asks nothing extra.
/// </summary>
public sealed partial class EditorSkillMinimumViewModel : ObservableObject
{
    public EditorSkillMinimumViewModel(int skillTypeId, string skillName, int level, int fitLevel)
    {
        SkillTypeId = skillTypeId;
        SkillName = skillName;
        FitLevel = fitLevel;
        _levelIndex = level - 1;
    }

    public static IReadOnlyList<string> LevelOptions { get; } = ["I", "II", "III", "IV", "V"];

    public int SkillTypeId { get; }
    public string SkillName { get; }
    public int FitLevel { get; }

    /// <summary>0-based index into <see cref="LevelOptions"/>, bound to the level ComboBox.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Level), nameof(FitHint), nameof(HasNoEffect))]
    private int _levelIndex;

    public int Level => LevelIndex + 1;

    /// <summary>The minimum is not above what the fit requires, so it changes nothing for anyone.</summary>
    public bool HasNoEffect => Level <= FitLevel;

    public string FitHint => HasNoEffect
        ? $"fit: {RomanLevel.Text(FitLevel)} · no effect"
        : FitLevel == 0 ? "fit needs —" : $"fit needs {RomanLevel.Text(FitLevel)}";
}
