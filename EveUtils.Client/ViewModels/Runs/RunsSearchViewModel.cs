using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>What the suggestions are counted over, and what one of them is called in the list in view.</summary>
public sealed record SearchPool(IReadOnlyList<ActivitySearchProfile> Profiles, string Singular, string Plural);

/// <summary>
/// The search box of the runs overview (ET-518). Typing filters live: the term is applied 150 ms after the last key, and
/// clearing applies at once. It matches on <see cref="ActivitySearchProfile"/>s, the unmarked text, so OPSEC never changes
/// what is found; what the screen does with a match is the owner's, through <c>applied</c>.
///
/// <para>The term lives as long as the screen does: it is neither saved nor restored by a restart.</para>
/// </summary>
public sealed partial class RunsSearchViewModel(Action applied, Func<SearchPool> pool, TimeSpan? debounce = null, TimeProvider? time = null)
    : ViewModelBase, IDisposable
{
    public const int MaxSuggestions = 6;

    private readonly TimeSpan _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private CancellationTokenSource? _pending;
    private bool _suggestionsDismissed;

    /// <summary>What is typed, as typed.</summary>
    [ObservableProperty] private string _text = string.Empty;

    /// <summary>The words the lists are filtered by: empty while the field is, and until the debounce has run out.</summary>
    public IReadOnlyList<string> Words { get; private set; } = [];

    public bool HasTerm => Words.Count > 0;

    public bool HasText => Text.Length > 0;

    public string TermText => string.Join(' ', Words);

    public ObservableCollection<SearchSuggestionViewModel> Suggestions { get; } = [];

    public bool ShowsSuggestions => !_suggestionsDismissed && Suggestions.Count > 0;

    public bool Matches(ActivitySearchProfile profile) => ActivitySearchTerms.Matches(profile.Text, Words);

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasText));
        _suggestionsDismissed = false;
        _RebuildSuggestions();
        _ScheduleApply();
    }

    [RelayCommand]
    public void Clear()
    {
        _suggestionsDismissed = true;
        Text = string.Empty;
        ApplyNow();
    }

    /// <summary>Applies what is typed without waiting for the debounce — Enter, a pick, a clear.</summary>
    public void ApplyNow()
    {
        _pending?.Cancel();
        string[] words = [.. ActivitySearchTerms.Words(Text)];
        if (words.SequenceEqual(Words, StringComparer.OrdinalIgnoreCase))
            return;

        Words = words;
        OnPropertyChanged(nameof(HasTerm));
        OnPropertyChanged(nameof(TermText));
        applied();
    }

    public void Pick(SearchSuggestionViewModel suggestion)
    {
        Text = suggestion.Term;
        _suggestionsDismissed = true;
        _RebuildSuggestions();
        ApplyNow();
    }

    /// <summary>↓ and ↑ through the suggestions; ↑ from the first returns to the field, with nothing highlighted.</summary>
    public void MoveSuggestion(int direction)
    {
        if (Suggestions.Count == 0)
            return;

        int current = Suggestions.ToList().FindIndex(suggestion => suggestion.IsHighlighted);
        int next = Math.Clamp(current + Math.Sign(direction), -1, Suggestions.Count - 1);
        for (int index = 0; index < Suggestions.Count; index++)
            Suggestions[index].IsHighlighted = index == next;
    }

    /// <summary>Enter: the highlighted suggestion if there is one, otherwise just the term as typed.</summary>
    public void Accept()
    {
        if (Suggestions.FirstOrDefault(suggestion => suggestion.IsHighlighted) is { } highlighted)
        {
            Pick(highlighted);
            return;
        }

        DismissSuggestions();
        ApplyNow();
    }

    public void DismissSuggestions()
    {
        _suggestionsDismissed = true;
        OnPropertyChanged(nameof(ShowsSuggestions));
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
    }

    private void _ScheduleApply()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        if (_debounce <= TimeSpan.Zero || Text.Length == 0)
        {
            _pending = null;
            ApplyNow();
            return;
        }

        var pending = _pending = new CancellationTokenSource();
        _ = _ApplyAfterDelayAsync(pending.Token);
    }

    private async Task _ApplyAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounce, _time, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ApplyNow();
    }

    /// <summary>The factions, families, types and sites that start with what is typed, with how many of the list in view
    /// they cover — factions and families first, since "Sleeper" is a better thing to search on than one site of it.</summary>
    private void _RebuildSuggestions()
    {
        string term = Text.Trim();
        IReadOnlyList<string> words = ActivitySearchTerms.Words(term);
        SearchSuggestionViewModel[] found = [];
        if (words.Count > 0)
        {
            SearchPool source = pool();
            found =
            [
                .. source.Profiles
                    .SelectMany(profile => new (SearchSuggestionKind Kind, string? Value)[]
                    {
                        (SearchSuggestionKind.Faction, profile.Faction),
                        (SearchSuggestionKind.Family, profile.Archetype),
                        (SearchSuggestionKind.Type, profile.TypeName),
                        (SearchSuggestionKind.Site, profile.Name)
                    })
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value)
                        && !string.Equals(candidate.Value, term, StringComparison.OrdinalIgnoreCase)
                        && ActivitySearchTerms.Matches(candidate.Value, words))
                    .GroupBy(candidate => (candidate.Kind, Value: candidate.Value!), SuggestionKey.Instance)
                    .OrderBy(group => group.Key.Kind)
                    .ThenByDescending(group => group.Count())
                    .ThenBy(group => group.Key.Value, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxSuggestions)
                    .Select(group => new SearchSuggestionViewModel(group.Key.Kind, group.Key.Value, group.Count(), source.Singular, source.Plural))
            ];
        }

        Suggestions.Clear();
        foreach (SearchSuggestionViewModel suggestion in found)
            Suggestions.Add(suggestion);
        OnPropertyChanged(nameof(ShowsSuggestions));
    }

    private sealed class SuggestionKey : IEqualityComparer<(SearchSuggestionKind Kind, string Value)>
    {
        public static SuggestionKey Instance { get; } = new();

        public bool Equals((SearchSuggestionKind Kind, string Value) left, (SearchSuggestionKind Kind, string Value) right) =>
            left.Kind == right.Kind && string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((SearchSuggestionKind Kind, string Value) key) =>
            HashCode.Combine(key.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Value));
    }
}
