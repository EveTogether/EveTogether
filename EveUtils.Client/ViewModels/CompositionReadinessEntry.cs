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
        IReadOnlyList<CompositionCharacterReadiness> characters)
    {
        RoleName = roleName;
        FitName = fitName;
        HullName = hullName;
        _characters = characters
            .OrderBy(character => character.Status)
            .ThenBy(character => character.ToFly ?? TimeSpan.MaxValue)
            .ThenBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        VisibleCharacters = new ObservableCollection<CompositionCharacterReadiness>(_characters);
        SelectedCharacter = _characters.FirstOrDefault();
    }

    public string RoleName { get; }
    public string FitName { get; }
    public string HullName { get; }
    public int CharacterCount => _characters.Count;
    public string CharactersLabel => $"YOUR {CharacterCount} CHARACTERS";
    public string AllLabel => $"ALL {CharacterCount} ›";
    public bool HasSearch => CharacterCount >= 9;
    public int ReadyCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Ready);
    public int FliesCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Flies);
    public int NotYetCount => _characters.Count(character => character.Status == CompositionReadinessStatus.NotYet);
    public int UnknownCount => _characters.Count(character => character.Status == CompositionReadinessStatus.Unknown);
    public string CountsLabel => $"✓ {ReadyCount} ready · {FliesCount} flies · {NotYetCount} not yet" +
                                 (UnknownCount > 0 ? $" · {UnknownCount} unknown" : "");
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
