using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Fleet;
using EveUtils.Client.Imaging;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One composition card in the compositions library. The list endpoint returns only the header, so the role-chips
/// and the roles/fits/min-pilots totals are loaded on demand from the graph (<see cref="LoadSummaryAsync"/>),
/// mirroring the on-demand row loading used elsewhere in the browser. <see cref="CanEdit"/> reflects the
/// owner-or-manage gate: own (and later server-granted) compositions can be opened in the editor;
/// others are read-only.
/// </summary>
public sealed partial class CompositionRowViewModel(
    FleetCompositionInfo info, string ownerName, bool canEdit, bool isLocal, IFleetCompositionClient client,
    ITypeImageProvider? images = null) : ObservableObject
{
    public long Id { get; } = info.Id;
    public int OwnerCharacterId { get; } = info.OwnerCharacterId;
    public bool IsLocal { get; } = isLocal;

    /// <summary>The facade this row was loaded through (local or a specific server), bound to the right context for
    /// its own actions (open/delete) — a server row deletes over gRPC, a local row over the repository.</summary>
    public IFleetCompositionClient Client { get; } = client;

    [ObservableProperty] private string _name = info.Name;
    public string? Description { get; } = info.Description;
    public string OwnerName { get; } = ownerName;
    public bool CanEdit { get; } = canEdit;

    /// <summary>How many fleets are coupled to this doctrine; 0 hides the pill.</summary>
    public int FleetCount { get; } = info.FleetCount;
    public bool HasFleets => FleetCount > 0;
    public string FleetCountLabel => FleetCount == 1 ? "⛴ 1 fleet" : $"⛴ {FleetCount} fleets";

    public ObservableCollection<CompositionRoleChipViewModel> RoleChips { get; } = [];

    /// <summary>The distinct ship hulls this doctrine flies, in first-seen order — a quick visual of what it fields.</summary>
    public ObservableCollection<CompositionHullViewModel> Hulls { get; } = [];

    [ObservableProperty] private int _roleCount;
    [ObservableProperty] private int _fitCount;
    [ObservableProperty] private int _minPilots;
    [ObservableProperty] private bool _summaryLoaded;

    /// <summary>Loads the role/fit summary (role-chips + totals) from the composition graph, once. The
    /// min-pilots total sums each role's requirement: its group minimum, or — when no group minimum is set —
    /// the sum of its per-fit minimums.</summary>
    public async Task LoadSummaryAsync()
    {
        if (SummaryLoaded)
            return;

        var detail = await Client.GetAsync(Id);
        if (detail is null)
            return;

        RoleChips.Clear();
        Hulls.Clear();
        var seenHulls = new HashSet<int>();
        var fits = 0;
        var min = 0;
        foreach (var role in detail.Roles)
        {
            RoleChips.Add(new CompositionRoleChipViewModel(role.RoleName, _FormatRoleMin(role)));
            fits += role.Entries.Count;
            min += _RoleRequirement(role);

            // One thumbnail per distinct hull across the whole doctrine (first-seen order), loaded on demand.
            foreach (var entry in role.Entries)
                if (seenHulls.Add(entry.Fit.ShipTypeId))
                {
                    var hull = new CompositionHullViewModel(entry.Fit.ShipTypeId, images);
                    Hulls.Add(hull);
                    _ = hull.LoadAsync();
                }
        }

        RoleCount = detail.Roles.Count;
        FitCount = fits;
        MinPilots = min;
        SummaryLoaded = true;
    }

    private static int _RoleRequirement(FleetCompositionRoleInfo role) =>
        role.GroupMinCount ?? role.Entries.Where(e => e.EntryMinCount is int).Sum(e => e.EntryMinCount!.Value);

    private static string _FormatRoleMin(FleetCompositionRoleInfo role)
    {
        if (role.GroupMinCount is int group)
            return "≥" + group;

        var perFit = role.Entries.Where(e => e.EntryMinCount is int).Select(e => e.EntryMinCount!.Value).ToList();
        return perFit.Count > 0 ? string.Join("+", perFit) : "—";
    }
}
