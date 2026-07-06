using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Fighters;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// The Fighter Bay panel for a carrier/supercarrier/Upwell structure: the launch tubes (attr 2216), the per-kind tube
/// limits (light 2217 / support 2218 / heavy 2219) and the bay capacity (2055). Squadrons are either loaded in a tube
/// (launched) or sitting in the bay as reserves; the panel can load a reserve into an open tube, unload a launched
/// squadron back to the bay, adjust a launched squadron's active fighters and drop squadrons entirely — all as a
/// simulation overlay (it never mutates the stored fit). Loading enforces the per-kind tube limits; the squadrons come
/// from the imported fit, so the bay never overflows through loading.
/// </summary>
public sealed partial class FighterBayViewModel : ViewModelBase
{
    private readonly int _lightLimit;
    private readonly int _supportLimit;
    private readonly int _heavyLimit;
    private readonly double _bayCapacity;
    private readonly Func<Task>? _onChanged;   // recomputes the fit stats after a launched-set change

    public ObservableCollection<FighterTubeViewModel> Tubes { get; } = [];
    public ObservableCollection<FighterSquadronViewModel> Reserves { get; } = [];

    public FighterBayViewModel(int tubeCount, int lightLimit, int supportLimit, int heavyLimit, double bayCapacity,
        Func<Task>? onChanged = null)
    {
        _lightLimit = lightLimit;
        _supportLimit = supportLimit;
        _heavyLimit = heavyLimit;
        _bayCapacity = bayCapacity;
        _onChanged = onChanged;
        for (var index = 1; index <= tubeCount; index++)
            Tubes.Add(new FighterTubeViewModel(index));
    }

    /// <summary>Places a squadron in the bay: into the next open tube its kind allows when <paramref name="launched"/>
    /// (the fit had it in a tube), otherwise as a reserve. Overflow past a kind limit falls back to a reserve.</summary>
    public void Seed(FighterSquadronViewModel squadron, bool launched)
    {
        if (launched && FirstOpenTubeFor(squadron.Kind) is { } tube)
            Place(squadron, tube);
        else
            Reserves.Add(squadron);
        RefreshTellers();
    }

    // ── Commands the panel binds to ──

    /// <summary>Loads a reserve squadron into the first open tube its kind allows; no-op when no tube is free for it.</summary>
    [RelayCommand]
    private void LoadFighter(FighterSquadronViewModel? squadron)
    {
        if (squadron is null || squadron.IsLaunched || FirstOpenTubeFor(squadron.Kind) is not { } tube)
            return;
        Reserves.Remove(squadron);
        Place(squadron, tube);
        AfterChange();
    }

    /// <summary>Loads the first compatible reserve into a specific open tube (the open-slot "+").</summary>
    [RelayCommand]
    private void FillTube(FighterTubeViewModel? tube)
    {
        if (tube is null || !tube.IsEmpty)
            return;
        var squadron = Reserves.FirstOrDefault(reserve => CanLaunch(reserve.Kind));
        if (squadron is null)
            return;
        Reserves.Remove(squadron);
        Place(squadron, tube);
        AfterChange();
    }

    /// <summary>Returns a launched squadron to the bay as a reserve.</summary>
    [RelayCommand]
    private void UnloadFighter(FighterSquadronViewModel? squadron)
    {
        if (squadron is null || !squadron.IsLaunched)
            return;
        UnloadToReserve(squadron);
        AfterChange();
    }

    /// <summary>Drops a squadron from the bay entirely (the reserve-row "x").</summary>
    [RelayCommand]
    private void RemoveFighter(FighterSquadronViewModel? squadron)
    {
        if (squadron is null)
            return;
        if (squadron.TubeIndex is { } index)
            Tubes[index - 1].Squadron = null;
        Reserves.Remove(squadron);
        AfterChange();
    }

    /// <summary>Empties every tube and the reserve list ("Remove All").</summary>
    [RelayCommand]
    private void RemoveAll()
    {
        foreach (var tube in Tubes)
            tube.Squadron = null;
        Reserves.Clear();
        AfterChange();
    }

    /// <summary>Adds one active fighter to a launched squadron (capped at the squadron size).</summary>
    [RelayCommand]
    private void IncreaseActive(FighterSquadronViewModel? squadron)
    {
        if (squadron is null || !squadron.IsLaunched || squadron.ActiveCount >= squadron.SquadronSize)
            return;
        squadron.ActiveCount++;
        AfterChange();
    }

    /// <summary>Removes one active fighter from a launched squadron, floored at zero. "-" never unloads the squadron —
    /// only the X button returns it to the bay.</summary>
    [RelayCommand]
    private void DecreaseActive(FighterSquadronViewModel? squadron)
    {
        if (squadron is null || !squadron.IsLaunched || squadron.ActiveCount <= 0)
            return;
        squadron.ActiveCount--;
        AfterChange();
    }

    // ── Tellers (the in-game header counts) ──

    // An Upwell structure carries a total tube count (2216) and bay (2055) but no per-kind limits (2217/2218/2219 are
    // absent) — any Standup fighter kind fills a tube, bounded only by the total. Detected by the per-kind limits being
    // absent while tubes exist.
    private bool StructureMode => _lightLimit <= 0 && _supportLimit <= 0 && _heavyLimit <= 0 && Tubes.Count > 0;

    public bool ShowLight => _lightLimit > 0;
    public bool ShowSupport => _supportLimit > 0;
    public bool ShowHeavy => _heavyLimit > 0;
    /// <summary>The single tube counter shown for a structure (which has no per-kind limits).</summary>
    public bool ShowTubeTotal => StructureMode;
    public string TubeTotalLabel => $"Tubes {TubesSummary}";
    public string LightLabel => $"{LaunchedOfKind(FighterKind.Light)}/{_lightLimit}";
    public string SupportLabel => $"{LaunchedOfKind(FighterKind.Support)}/{_supportLimit}";
    public string HeavyLabel => $"{LaunchedOfKind(FighterKind.Heavy)}/{_heavyLimit}";

    /// <summary>The bay volume in use: every squadron present (tube or reserve) occupies its full volume.</summary>
    public double BayUsed => Tubes.Where(t => t.Squadron is not null).Sum(t => t.Squadron!.BayVolume)
                             + Reserves.Sum(reserve => reserve.BayVolume);
    public double BayCapacity => _bayCapacity;

    /// <summary>"used / capacity m³" for the panel footer.</summary>
    public string BayLabel => $"{BayUsed:N0} / {_bayCapacity:N0} m³";

    /// <summary>"launched / tubes" for the bar button.</summary>
    public string TubesSummary => $"{Tubes.Count(tube => tube.Squadron is not null)}/{Tubes.Count}";

    /// <summary>The fighters waiting in the bay (not in a tube), shown under the reserve list.</summary>
    public string ReserveSummary => $"{Reserves.Sum(reserve => reserve.SquadronSize)} fighters in bay";

    /// <summary>The launched squadrons the DPS engine sees: each tube's squadron with its active fighter count.</summary>
    public IReadOnlyList<FighterInput> LaunchedFighters => Tubes
        .Where(tube => tube.Squadron is not null)
        .Select(tube => new FighterInput(tube.Squadron!.TypeId, tube.Squadron.ActiveCount))
        .ToList();

    public bool HasFighters => Tubes.Any(tube => tube.Squadron is not null) || Reserves.Count > 0;

    /// <summary>Hands each squadron its type's resolved per-fighter readout so the tube tooltip shows DPS + range.</summary>
    public void ApplyContributions(IReadOnlyList<FighterContribution> contributions)
    {
        foreach (var squadron in Tubes.Where(tube => tube.Squadron is not null).Select(tube => tube.Squadron!).Concat(Reserves))
            squadron.SetContribution(contributions.FirstOrDefault(contribution => contribution.TypeId == squadron.TypeId));
    }

    private int LaunchedOfKind(FighterKind kind) => Tubes.Count(tube => tube.Squadron?.Kind == kind);

    private int LimitFor(FighterKind kind)
    {
        if (StructureMode)
            return Tubes.Count;   // no per-kind cap — any kind fills tubes up to the total (the open-tube check bounds it)
        return kind switch
        {
            FighterKind.Light => _lightLimit,
            FighterKind.Support => _supportLimit,
            FighterKind.Heavy => _heavyLimit,
            _ => 0
        };
    }

    // A kind can launch another squadron when it is under its per-kind tube limit. The open-tube check (FirstOpenTubeFor)
    // also bounds it by the total tube count, so both the per-type counter and the tube total are enforced.
    private bool CanLaunch(FighterKind kind) => LaunchedOfKind(kind) < LimitFor(kind);

    private FighterTubeViewModel? FirstOpenTubeFor(FighterKind kind) =>
        CanLaunch(kind) ? Tubes.FirstOrDefault(tube => tube.IsEmpty) : null;

    private static void Place(FighterSquadronViewModel squadron, FighterTubeViewModel tube)
    {
        tube.Squadron = squadron;
        squadron.TubeIndex = tube.Index;
    }

    private void UnloadToReserve(FighterSquadronViewModel squadron)
    {
        if (squadron.TubeIndex is not { } index)
            return;
        Tubes[index - 1].Squadron = null;
        squadron.TubeIndex = null;
        squadron.ActiveCount = squadron.SquadronSize;   // a re-loaded squadron launches at full strength again
        Reserves.Add(squadron);
    }

    private void AfterChange()
    {
        RefreshTellers();
        _ = _onChanged?.Invoke();
    }

    private void RefreshTellers()
    {
        OnPropertyChanged(nameof(LightLabel));
        OnPropertyChanged(nameof(SupportLabel));
        OnPropertyChanged(nameof(HeavyLabel));
        OnPropertyChanged(nameof(BayUsed));
        OnPropertyChanged(nameof(BayLabel));
        OnPropertyChanged(nameof(TubesSummary));
        OnPropertyChanged(nameof(TubeTotalLabel));
        OnPropertyChanged(nameof(ReserveSummary));
        OnPropertyChanged(nameof(HasFighters));
    }
}
