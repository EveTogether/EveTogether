using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;

namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>
/// The + FROM DOCTRINE… picker (ET-386): composition, then role, then entry. Every composition's whole graph is
/// loaded once by <see cref="CreateAsync"/> — through <see cref="IFleetCompositionReader"/>, never the write-
/// repository (ET-383 guard) — so the role/entry cascade that follows a selection is a plain in-memory filter, with
/// nothing left to await from a property setter.
/// </summary>
public sealed partial class DoctrinePickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<FleetCompositionGraph> _graphs;

    private DoctrinePickerViewModel(IReadOnlyList<FleetCompositionGraph> graphs)
    {
        _graphs = graphs;
        foreach (var graph in graphs)
        {
            Compositions.Add(graph.Composition);
        }
    }

    /// <summary>Loads every composition that has at least one fit entry, with its role/entry graph, ready for the
    /// picker's cascading selection.</summary>
    public static async Task<DoctrinePickerViewModel> CreateAsync(
        IFleetCompositionReader reader, CancellationToken cancellationToken = default)
    {
        var graphs = new List<FleetCompositionGraph>();
        foreach (var composition in await reader.ListAllAsync(cancellationToken))
        {
            if (await reader.GetGraphAsync(composition.Id, cancellationToken) is { } graph
                && graph.Roles.Any(role => role.Entries.Count > 0))
            {
                graphs.Add(graph);
            }
        }

        return new DoctrinePickerViewModel(graphs);
    }

    public ObservableCollection<FleetComposition> Compositions { get; } = [];
    public ObservableCollection<FleetCompositionRole> Roles { get; } = [];
    public ObservableCollection<FleetCompositionEntry> Entries { get; } = [];

    [ObservableProperty] private FleetComposition? _selectedComposition;
    [ObservableProperty] private FleetCompositionRole? _selectedRole;
    [ObservableProperty] private FleetCompositionEntry? _selectedEntry;

    public bool CanConfirm => SelectedEntry is not null;

    partial void OnSelectedCompositionChanged(FleetComposition? value)
    {
        Roles.Clear();
        SelectedRole = null;
        var graph = value is null ? null : _graphs.FirstOrDefault(g => g.Composition.Id == value.Id);
        foreach (var roleGraph in graph?.Roles.Where(role => role.Entries.Count > 0) ?? [])
        {
            Roles.Add(roleGraph.Role);
        }
    }

    partial void OnSelectedRoleChanged(FleetCompositionRole? value)
    {
        Entries.Clear();
        SelectedEntry = null;
        var graph = SelectedComposition is null ? null : _graphs.FirstOrDefault(g => g.Composition.Id == SelectedComposition.Id);
        var roleGraph = value is null ? null : graph?.Roles.FirstOrDefault(role => role.Role.Id == value.Id);
        foreach (var entry in roleGraph?.Entries ?? [])
        {
            Entries.Add(entry);
        }
    }

    partial void OnSelectedEntryChanged(FleetCompositionEntry? value) => OnPropertyChanged(nameof(CanConfirm));

    /// <summary>The confirmed pick — the entry plus its composition/role names for the "&lt;composition&gt; ·
    /// &lt;role&gt; · &lt;fit&gt;" label. Null until a composition, a role and an entry are all selected.</summary>
    public DoctrineEntryPick? BuildPick() => SelectedComposition is null || SelectedRole is null || SelectedEntry is null
        ? null
        : new DoctrineEntryPick(SelectedEntry, SelectedComposition.Name, SelectedRole.RoleName);
}

/// <summary>The + FROM DOCTRINE… picker's result (ET-386): the chosen entry plus the composition/role names for the
/// "&lt;composition&gt; · &lt;role&gt; · &lt;fit&gt;" label and the entry id used as the plan row's SourceRef.</summary>
public sealed record DoctrineEntryPick(FleetCompositionEntry Entry, string CompositionName, string RoleName);
