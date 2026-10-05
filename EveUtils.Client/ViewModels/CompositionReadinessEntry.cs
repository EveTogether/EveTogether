using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;

namespace EveUtils.Client.ViewModels;

public sealed partial class CompositionReadinessEntry : ObservableObject
{
    private readonly IReadOnlyList<CompositionCharacterReadiness> _characters;

    public CompositionReadinessEntry(string roleName, string fitName, string hullName,
        IReadOnlyList<CompositionCharacterReadiness> characters, bool hasSkillMinimums = false)
    {
        RoleName = roleName;
        HasSkillMinimums = hasSkillMinimums;
        FitName = fitName;
        HullName = hullName;
        _characters = characters
            .OrderBy(character => character.Status)
            .ThenBy(character => character.ToFly ?? TimeSpan.MaxValue)
            .ThenBy(character => character.ToMin ?? TimeSpan.MaxValue)
            .ThenBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        VisibleCharacters = new ObservableCollection<CompositionCharacterReadiness>(_characters);
        SelectedCharacter = _characters.FirstOrDefault();
    }

    public string RoleName { get; }
    public string FitName { get; }
    public string HullName { get; }
    public string HullLabel => HullName.ToUpperInvariant();

    /// <summary>The fit entry carries a doctrine skill minimum, so the TO MIN column means something.</summary>
    public bool HasSkillMinimums { get; }

    /// <summary>The level the fit itself requires of each skill, prerequisites included.</summary>
    public IReadOnlyDictionary<int, int> FitSkillLevels { get; init; } = new Dictionary<int, int>();
    public string RequiredSkillsLabel => $"{FitSkillLevels.Count} skills required by the fit";
    public string FitRequiresText { get; init; } = "";
    public IReadOnlyList<string> MinimumChips { get; init; } = [];
    public bool HasNoMinimum => MinimumChips.Count == 0;

    /// <summary>The stored entry behind this readiness row, and the doctrine around it; set by the module.</summary>
    public FleetCompositionEntryInfo? Entry { get; set; }
    public string CompositionName { get; set; } = "";
    public bool CanEdit { get; set; }

    /// <summary>"MAINLINE DPS  ≥ 10 pilots" heads the first entry of each role.</summary>
    public bool IsFirstInRole { get; set; }
    public string RoleHeader => RoleName.ToUpperInvariant();
    public string RoleMinLabel { get; set; } = "";
    [ObservableProperty] private bool _isSelected;

    public int CharacterCount => _characters.Count;
    public string CharactersLabel => $"YOUR {CharacterCount} CHARACTERS";
    public string DetailCharactersLabel => $"YOUR CHARACTERS · {CharacterCount}";
    public string SearchPlaceholder => $"Search {CharacterCount} characters…";
    public string AllLabel => $"ALL {CharacterCount} ›";
    public bool HasSearch => CharacterCount >= CharacterPickerSearch.SearchThreshold;
    public int ReadyCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Ready);
    public int FliesCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Flies);
    public int NotYetCount => _characters.Count(character => character.Status == CompositionReadinessStatus.NotYet);
    public int UnknownCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Unknown);
    public string ReadyLabel => $"✓ {ReadyCount} ready";
    public string FliesLabel => $"{FliesCount} fly, below min";
    public string NotYetLabel => $"{NotYetCount} not yet";
    public string UnknownLabel => $"{UnknownCount} unknown";
    public bool HasUnknown => UnknownCount > 0;
    private CompositionCharacterReadiness? _NextToFly => _characters.FirstOrDefault(character =>
        character.Status == CompositionReadinessStatus.NotYet && character.ToFly is not null);
    public bool HasNextToFly => _NextToFly is not null;
    public string NextToFlyName => _NextToFly?.Name ?? "";
    public string NextToFlyTime => _NextToFly?.ToFlyLabel ?? "";
    public string NextToFlyLabel => _NextToFly is { } next ? $"next to fly: {next.Name} in {next.ToFlyLabel}" : "";

    public ObservableCollection<CompositionCharacterReadiness> VisibleCharacters { get; }
    [ObservableProperty] private CompositionCharacterReadiness? _selectedCharacter;
    [ObservableProperty] private string _searchText = "";

    /// <summary>Picks the character by name, for a reload that should land back on the same row.</summary>
    public void SelectByName(string? name)
    {
        if (_characters.FirstOrDefault(character => character.Name == name) is { } match)
        {
            SelectedCharacter = match;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        VisibleCharacters.Clear();
        foreach (CompositionCharacterReadiness character in _characters.Where(character =>
                     character.Name.Contains(value, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleCharacters.Add(character);
        }
        if (SelectedCharacter is not null && !VisibleCharacters.Contains(SelectedCharacter))
        {
            SelectedCharacter = VisibleCharacters.FirstOrDefault();
        }
    }
}
