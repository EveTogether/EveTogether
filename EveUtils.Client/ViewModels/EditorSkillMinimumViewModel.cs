using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [NotifyPropertyChangedFor(nameof(Level), nameof(FitHint), nameof(HasNoEffect), nameof(LevelChoices))]
    private int _levelIndex;

    public int Level => Math.Clamp(LevelIndex + 1, 1, 5);

    /// <summary>The minimum is not above what the fit requires, so it changes nothing for anyone.</summary>
    public bool HasNoEffect => Level <= FitLevel;

    public string FitHint => HasNoEffect
        ? $"fit: {RomanLevel.Text(FitLevel)} · no effect"
        : FitLevel == 0 ? "new" : $"fit needs {RomanLevel.Text(FitLevel)}";

    /// <summary>The I–V segmented selector, the current level lit.</summary>
    public IReadOnlyList<SkillLevelChoice> LevelChoices => [.. Enumerable.Range(1, 5).Select(level => new SkillLevelChoice(this, level))];

    [RelayCommand]
    private void SetLevel(int level) => LevelIndex = Math.Clamp(level, 1, 5) - 1;
}

/// <summary>One button of the I–V level selector.</summary>
public sealed record SkillLevelChoice(EditorSkillMinimumViewModel Owner, int Level)
{
    public string Text => RomanLevel.Text(Level);
    public bool IsOn => Owner.Level == Level;
}
