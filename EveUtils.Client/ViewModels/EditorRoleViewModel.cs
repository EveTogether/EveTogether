using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// A role group in the composition editor: a label, an optional group minimum, and its fit entries. The role's
/// pilot requirement is its group minimum, or — when no group minimum is set — the sum of its per-fit minimums (the
/// same two-level rule the library summary uses). <see cref="Id"/> is null until the role is persisted on save.
/// </summary>
public sealed partial class EditorRoleViewModel : ObservableObject
{
    public EditorRoleViewModel(long? id, string roleName, int? groupMinCount)
    {
        Id = id;
        _roleName = roleName;
        _groupMinText = CompositionMinValue.Format(groupMinCount);
    }

    public long? Id { get; }

    [ObservableProperty] private string _roleName;
    [ObservableProperty] private string _groupMinText;

    public ObservableCollection<EditorEntryViewModel> Entries { get; } = [];

    /// <summary>The parsed group minimum, or null when the field is blank.</summary>
    public int? GroupMinCount => CompositionMinValue.Parse(GroupMinText);

    /// <summary>Pilots this role needs: the group minimum, else the sum of the per-fit minimums.</summary>
    public int Requirement => GroupMinCount ?? Entries.Where(e => e.EntryMinCount is int).Sum(e => e.EntryMinCount!.Value);

    /// <summary>Raised whenever this role's <see cref="Requirement"/> or fit count may have changed, so the editor can
    /// refresh its live footer total.</summary>
    public event Action? Changed;

    partial void OnGroupMinTextChanged(string value) => Changed?.Invoke();

    /// <summary>Adds an entry and tracks its min edits so the footer total stays live.</summary>
    public void Add(EditorEntryViewModel entry)
    {
        entry.PropertyChanged += _OnEntryChanged;
        Entries.Add(entry);
        Changed?.Invoke();
    }

    public void Remove(EditorEntryViewModel entry)
    {
        entry.PropertyChanged -= _OnEntryChanged;
        Entries.Remove(entry);
        Changed?.Invoke();
    }

    private void _OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorEntryViewModel.MinText))
            Changed?.Invoke();
    }
}
