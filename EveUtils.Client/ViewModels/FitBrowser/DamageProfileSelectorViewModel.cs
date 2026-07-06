using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// Damage-profile selector for the DEFENSE panel. A single dropdown ("vs [name ratio]") that opens a
/// tabbed picker with three modes:
/// <list type="bullet">
///   <item><b>Presets</b> — Uniform + faction presets (SDE-derived live), grouped and searchable.</item>
///   <item><b>NPC types</b> — free-text search over ~3518 NPC types (category 11, damage-attrs only).</item>
///   <item><b>Custom</b> — four numeric EM/Th/Kin/Exp inputs, normalised.</item>
/// </list>
/// The parent ViewModel subscribes to <see cref="ProfileChanged"/> to trigger a recompute and reads
/// <see cref="CurrentProfile"/> + <see cref="ProfileLabel"/>.
/// </summary>
public sealed class DamageProfileSelectorViewModel : ViewModelBase
{
    private readonly ISdeAccessor? _sde;
    private readonly IReadOnlyList<PresetGroup> _allGroups;

    public DamageProfileSelectorViewModel(ISdeAccessor? sde)
    {
        _sde = sde;
        _allGroups = BuildPresetGroups(sde);
        PresetGroups = new ObservableCollection<PresetGroup>(_allGroups);

        ToggleOpenCommand = new RelayCommand(() => IsOpen = !IsOpen);
        SetModeCommand = new RelayCommand<string>(SetMode);
        SelectPresetCommand = new RelayCommand<DamageProfilePresetViewModel>(SelectPreset);
        SelectNpcCommand = new RelayCommand<NpcEnemyViewModel>(SelectNpc);

        // Default: Uniform.
        _selectedPreset = _allGroups.SelectMany(g => g.Items).FirstOrDefault();
        ApplyProfile(DamageProfile.Uniform, "Uniform", raise: false);
    }

    // ── Dropdown open/close ──────────────────────────────────────────────────────────────────────────
    private bool _isOpen;
    public bool IsOpen { get => _isOpen; set => SetProperty(ref _isOpen, value); }
    public ICommand ToggleOpenCommand { get; }

    // ── Mode ─────────────────────────────────────────────────────────────────────────────────────────
    private DamageProfileMode _mode = DamageProfileMode.Presets;
    public DamageProfileMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsPresetsMode));
            OnPropertyChanged(nameof(IsNpcMode));
            OnPropertyChanged(nameof(IsCustomMode));
        }
    }

    public bool IsPresetsMode => _mode == DamageProfileMode.Presets;
    public bool IsNpcMode     => _mode == DamageProfileMode.Npc;
    public bool IsCustomMode  => _mode == DamageProfileMode.Custom;

    /// <summary>Switches mode. CommandParameter: "0"=Presets, "1"=Custom, "2"=Npc.</summary>
    public ICommand SetModeCommand { get; }

    private void SetMode(string? p) =>
        Mode = p switch
        {
            "1" => DamageProfileMode.Custom,
            "2" => DamageProfileMode.Npc,
            _   => DamageProfileMode.Presets,
        };

    // ── Presets (grouped + searchable) ───────────────────────────────────────────────────────────────
    public ObservableCollection<PresetGroup> PresetGroups { get; }

    private DamageProfilePresetViewModel? _selectedPreset;
    public DamageProfilePresetViewModel? SelectedPreset
    {
        get => _selectedPreset;
        private set => SetProperty(ref _selectedPreset, value);
    }

    public ICommand SelectPresetCommand { get; }

    private void SelectPreset(DamageProfilePresetViewModel? preset)
    {
        if (preset is null) return;
        SelectedPreset = preset;
        Mode = DamageProfileMode.Presets;
        ApplyProfile(preset.Profile, preset.Name);
        IsOpen = false;
    }

    private string _presetSearchText = string.Empty;
    public string PresetSearchText
    {
        get => _presetSearchText;
        set { if (SetProperty(ref _presetSearchText, value)) FilterPresets(); }
    }

    private void FilterPresets()
    {
        var f = _presetSearchText.Trim();
        PresetGroups.Clear();
        foreach (var g in _allGroups)
        {
            var items = string.IsNullOrEmpty(f)
                ? g.Items
                : g.Items.Where(i => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count > 0)
                PresetGroups.Add(new PresetGroup(g.Header, items));
        }
    }

    // ── NPC types (searchable) ───────────────────────────────────────────────────────────────────────
    private string _npcSearchText = string.Empty;
    public string NpcSearchText
    {
        get => _npcSearchText;
        set { if (SetProperty(ref _npcSearchText, value)) RefreshNpcResults(); }
    }

    public ObservableCollection<NpcEnemyViewModel> NpcResults { get; } = [];

    private NpcEnemyViewModel? _selectedNpc;
    public NpcEnemyViewModel? SelectedNpc
    {
        get => _selectedNpc;
        private set => SetProperty(ref _selectedNpc, value);
    }

    public ICommand SelectNpcCommand { get; }

    private void SelectNpc(NpcEnemyViewModel? npc)
    {
        if (npc is null) return;
        SelectedNpc = npc;
        Mode = DamageProfileMode.Npc;
        ApplyProfile(npc.Profile, npc.Name);
        IsOpen = false;
    }

    private void RefreshNpcResults()
    {
        NpcResults.Clear();
        if (string.IsNullOrWhiteSpace(_npcSearchText) || _sde is null)
            return;
        foreach (var npc in _sde.SearchNpcEnemies(_npcSearchText))
        {
            var profile = _sde.GetNpcDamageProfile(npc.TypeId);
            if (profile is null) continue;
            NpcResults.Add(new NpcEnemyViewModel(npc, profile));
        }
    }

    // ── Custom ───────────────────────────────────────────────────────────────────────────────────────
    private string _customEm = "25", _customTh = "35", _customKin = "25", _customExp = "15";
    public string CustomEm  { get => _customEm;  set { if (SetProperty(ref _customEm,  value)) ApplyCustom(); } }
    public string CustomTh  { get => _customTh;  set { if (SetProperty(ref _customTh,  value)) ApplyCustom(); } }
    public string CustomKin { get => _customKin; set { if (SetProperty(ref _customKin, value)) ApplyCustom(); } }
    public string CustomExp { get => _customExp; set { if (SetProperty(ref _customExp, value)) ApplyCustom(); } }

    private void ApplyCustom()
    {
        if (_mode != DamageProfileMode.Custom) return;
        double em = TryParse(_customEm), th = TryParse(_customTh), kin = TryParse(_customKin), exp = TryParse(_customExp);
        if (em + th + kin + exp <= 0) return;
        ApplyProfile(new DamageProfile(em, th, kin, exp).Normalized(), "Custom");
    }

    // ── Active profile + display ─────────────────────────────────────────────────────────────────────
    private DamageProfile _currentProfile = DamageProfile.Uniform;
    public DamageProfile CurrentProfile => _currentProfile;

    /// <summary>Label shown after EFFECTIVE HP ("· vs X"); the literal "uniform" suppresses the suffix.</summary>
    public string ProfileLabel { get; private set; } = "uniform";

    /// <summary>Profile name shown in the closed dropdown.</summary>
    public string DisplayName { get; private set; } = "Uniform";

    // Tinted ratio shown in the dropdown (whole-percent integers).
    public int PctEm { get; private set; } = 25;
    public int PctTh { get; private set; } = 25;
    public int PctKin { get; private set; } = 25;
    public int PctExp { get; private set; } = 25;

    public event EventHandler? ProfileChanged;

    private void ApplyProfile(DamageProfile profile, string name, bool raise = true)
    {
        _currentProfile = profile;
        DisplayName = name;
        // "Uniform" suppresses the "· vs" suffix on EFFECTIVE HP (see FitDetailWindowViewModel.TotalEhp).
        ProfileLabel = name.Equals("Uniform", StringComparison.OrdinalIgnoreCase) ? "uniform" : name;
        PctEm  = (int)Math.Round(profile.Em  * 100);
        PctTh  = (int)Math.Round(profile.Th  * 100);
        PctKin = (int)Math.Round(profile.Kin * 100);
        PctExp = (int)Math.Round(profile.Exp * 100);

        OnPropertyChanged(nameof(CurrentProfile));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ProfileLabel));
        OnPropertyChanged(nameof(PctEm));
        OnPropertyChanged(nameof(PctTh));
        OnPropertyChanged(nameof(PctKin));
        OnPropertyChanged(nameof(PctExp));

        if (raise) ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Preset building ──────────────────────────────────────────────────────────────────────────────
    private static readonly string[] PirateFactions =
        ["Guristas", "Angel Cartel", "Sansha's Nation", "Blood Raiders", "Serpentis"];
    private static readonly string[] EmpireNavies =
        ["Amarr Empire", "Caldari State", "Gallente Federation", "Minmatar Republic"];

    private static IReadOnlyList<PresetGroup> BuildPresetGroups(ISdeAccessor? sde)
    {
        var generic = new List<DamageProfilePresetViewModel>
        {
            new("Uniform", DamageProfile.Uniform),
            // All-zero profile = "Raw HP": the engine ignores resists and reports the raw shield+armor+hull buffer,
            // an NPC-independent baseline.
            new("Raw HP", new DamageProfile(0, 0, 0, 0)),
        };
        var pirate = new List<DamageProfilePresetViewModel>();
        var navies = new List<DamageProfilePresetViewModel>();
        var other  = new List<DamageProfilePresetViewModel>();

        foreach (var (name, like) in NpcFactionPresets.FactionPatterns)
        {
            var profile = ResolveFactionProfile(sde, name, like);
            var vm = new DamageProfilePresetViewModel(name, profile);
            if (PirateFactions.Contains(name)) pirate.Add(vm);
            else if (EmpireNavies.Contains(name)) navies.Add(vm);
            else other.Add(vm);
        }

        var groups = new List<PresetGroup> { new("GENERIC", generic) };
        if (pirate.Count > 0) groups.Add(new PresetGroup("PIRATE FACTIONS", pirate));
        if (other.Count  > 0) groups.Add(new PresetGroup("OTHER", other));
        if (navies.Count > 0) groups.Add(new PresetGroup("EMPIRE NAVIES", navies));
        return groups;
    }

    private static DamageProfile ResolveFactionProfile(ISdeAccessor? sde, string name, string like)
    {
        if (sde is { IsAvailable: true })
        {
            double em = 0, th = 0, kin = 0, exp = 0;
            foreach (var npc in sde.SearchNpcEnemies(like.Trim('%')))
            {
                var p = sde.GetNpcDamageProfile(npc.TypeId);
                if (p is null) continue;
                em += p.Em; th += p.Th; kin += p.Kin; exp += p.Exp;
            }
            if (em + th + kin + exp > 0)
                return new DamageProfile(em, th, kin, exp).Normalized();
        }
        return NpcFactionPresets.GoldenProfiles.TryGetValue(name, out var golden) ? golden : DamageProfile.Uniform;
    }

    private static double TryParse(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : 0;
}

/// <summary>The three modes of the damage-profile selector.</summary>
public enum DamageProfileMode
{
    Presets,
    Custom,
    Npc,
}

/// <summary>A named, grouped collection of presets shown under one header in the Presets tab.</summary>
public sealed record PresetGroup(string Header, IReadOnlyList<DamageProfilePresetViewModel> Items);

/// <summary>A preset row: name + the profile, with whole-percent EM/Th/Kin/Exp for the tinted ratio.</summary>
public sealed class DamageProfilePresetViewModel
{
    public DamageProfilePresetViewModel(string name, DamageProfile profile)
    {
        Name = name;
        Profile = profile;
        PctEm  = (int)Math.Round(profile.Em  * 100);
        PctTh  = (int)Math.Round(profile.Th  * 100);
        PctKin = (int)Math.Round(profile.Kin * 100);
        PctExp = (int)Math.Round(profile.Exp * 100);
    }

    public string Name { get; }
    public DamageProfile Profile { get; }
    public int PctEm { get; }
    public int PctTh { get; }
    public int PctKin { get; }
    public int PctExp { get; }
}
