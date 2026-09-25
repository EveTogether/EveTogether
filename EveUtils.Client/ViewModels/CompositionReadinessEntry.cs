using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

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

    /// <summary>The fit entry carries a doctrine skill minimum, so the TO MIN column means something.</summary>
    public bool HasSkillMinimums { get; }
    public int CharacterCount => _characters.Count;
    public string CharactersLabel => $"YOUR {CharacterCount} CHARACTERS";
    public string DetailCharactersLabel => $"YOUR CHARACTERS · {CharacterCount}";
    public string SearchPlaceholder => $"Search {CharacterCount} characters…";
    public string AllLabel => $"ALL {CharacterCount} ›";
    public bool HasSearch => CharacterCount >= 9;
    public int ReadyCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Ready);
    public int FliesCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Flies);
    public int NotYetCount => _characters.Count(character => character.Status == CompositionReadinessStatus.NotYet);
    public int UnknownCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Unknown);
    public string ReadyLabel => $"✓ {ReadyCount} ready";
    public string FliesLabel => $"{FliesCount} flies";
    public string NotYetLabel => $"{NotYetCount} not yet";
    public string UnknownLabel => $"{UnknownCount} unknown";
    public bool HasUnknown => UnknownCount > 0;
    public string NextToFlyLabel => _characters.FirstOrDefault(character =>
        character.Status == CompositionReadinessStatus.NotYet && character.ToFly is not null) is { } next
        ? $"next to fly: {next.Name} in {next.ToFlyLabel}"
        : "";

    public ObservableCollection<CompositionCharacterReadiness> VisibleCharacters { get; }
    [ObservableProperty] private CompositionCharacterReadiness? _selectedCharacter;
    [ObservableProperty] private string _searchText = "";

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
