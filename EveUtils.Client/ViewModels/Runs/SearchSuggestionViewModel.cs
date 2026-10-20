using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Opsec;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One line under the runs search: a faction, family, type or site the pilot has flown, and how often.</summary>
public sealed partial class SearchSuggestionViewModel : ViewModelBase
{
    public SearchSuggestionViewModel(SearchSuggestionKind kind, string term, int count, string singular, string plural)
    {
        Kind = kind;
        Term = term;
        // Only a site is a location; the name is masked on screen and typed unmasked into the field when picked.
        DisplayText = kind is SearchSuggestionKind.Site ? OpsecText.Mark(term) : term;
        KindText = kind switch
        {
            SearchSuggestionKind.Faction => "FACTION",
            SearchSuggestionKind.Family => "FAMILY",
            SearchSuggestionKind.Type => "TYPE",
            _ => "SITE"
        };
        CountText = $"{count} {(count == 1 ? singular : plural)}";
    }

    public SearchSuggestionKind Kind { get; }

    /// <summary>What goes into the field when this is picked: unmarked, so it matches.</summary>
    public string Term { get; }

    public string DisplayText { get; }

    public string KindText { get; }

    public string CountText { get; }

    [ObservableProperty] private bool _isHighlighted;
}
