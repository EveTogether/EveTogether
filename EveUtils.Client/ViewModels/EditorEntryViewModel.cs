using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A fit entry inside a role group in the composition editor. <see cref="Id"/> is null for a fit added during
/// this edit session (persisted on save). The <see cref="FitReferenceInfo"/> snapshot is immutable — changing a fit
/// means removing this entry and picking another — so only the optional per-fit minimum and the doctrine skill
/// minimums are editable here.
/// </summary>
public sealed partial class EditorEntryViewModel : ObservableObject
{
    private readonly ITypeImageProvider? _images;
    private readonly IReadOnlyDictionary<int, int> _fitSkillLevels;

    public EditorEntryViewModel(long? id, FitReferenceInfo fit, string hullName, int? entryMinCount,
        ITypeImageProvider? images = null, IReadOnlyList<SkillMinimum>? skillMinimums = null,
        IReadOnlyDictionary<int, int>? fitSkillLevels = null, Func<int, string>? skillName = null)
    {
        Id = id;
        Fit = fit;
        HullName = hullName;
        _minText = CompositionMinValue.Format(entryMinCount);
        _images = images;
        _fitSkillLevels = fitSkillLevels ?? new Dictionary<int, int>();
        foreach (var minimum in skillMinimums ?? [])
        {
            AddSkillMinimum(minimum.SkillTypeId, skillName?.Invoke(minimum.SkillTypeId) ?? $"type {minimum.SkillTypeId}", minimum.Level);
        }
    }

    public long? Id { get; }
    public FitReferenceInfo Fit { get; }
    public string FitName => Fit.FitName;
    public string HullName { get; }

    [ObservableProperty] private string _minText;

    /// <summary>The parsed per-fit minimum, or null when the field is blank.</summary>
    public int? EntryMinCount => CompositionMinValue.Parse(MinText);

    /// <summary>The doctrine skill minimums, tentative until the editor saves.</summary>
    public ObservableCollection<EditorSkillMinimumViewModel> SkillMinimums { get; } = [];

    public IReadOnlyList<SkillMinimum> SkillMinimumList =>
        [.. SkillMinimums.Select(row => new SkillMinimum(row.SkillTypeId, row.Level))];

    /// <summary>The skill name typed into the add row's picker.</summary>
    [ObservableProperty] private string _newSkillText = "";

    /// <summary>0-based level index for the add row (default V).</summary>
    [ObservableProperty] private int _newSkillLevelIndex = 4;

    /// <summary>Adds a minimum, or raises the level of the one already there for the skill (one row per skill).</summary>
    public void AddSkillMinimum(int skillTypeId, string skillName, int level)
    {
        var existing = SkillMinimums.FirstOrDefault(row => row.SkillTypeId == skillTypeId);
        if (existing is not null)
        {
            existing.LevelIndex = Math.Max(existing.LevelIndex, level - 1);
            return;
        }

        SkillMinimums.Add(new EditorSkillMinimumViewModel(skillTypeId, skillName, level,
            _fitSkillLevels.GetValueOrDefault(skillTypeId)));
    }

    [RelayCommand]
    private void RemoveSkillMinimum(EditorSkillMinimumViewModel? row)
    {
        if (row is not null)
        {
            SkillMinimums.Remove(row);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHullImage))]
    private Bitmap? _hullImage;

    public bool HasHullImage => HullImage is not null;

    /// <summary>Loads the hull render for the entry icon — on demand, opt-in CCP images; null leaves the
    /// placeholder box.</summary>
    public async Task LoadHullImageAsync() =>
        HullImage = _images is null ? null : await _images.GetImageAsync(Fit.ShipTypeId, TypeImageKind.Render, 64);
}
