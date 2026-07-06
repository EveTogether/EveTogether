using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings;

namespace EveUtils.Client.ViewModels;

public partial class CharacterViewModel : ObservableObject
{
    public int CharacterId { get; }
    public string Name { get; }
    public string OwnerId => CharacterId.ToString();

    /// <summary>
    /// True when this character has no ESI link (no <c>EsiCharacterId</c>) — e.g. surfaced from a gamelog only
    /// . Local-only: DPS is visible locally, but server-coupling is not offered until it signs in via ESI.
    /// </summary>
    public bool IsLocalOnly { get; }

    [ObservableProperty] private bool _hasReadFittings;
    [ObservableProperty] private bool _hasWriteFittings;

    /// <summary>Local: has a locally stored ESI token (Mode A).</summary>
    [ObservableProperty] private bool _isLocal;

    /// <summary>True when a running EVE client on THIS machine is detected for this character (window title or
    /// launcher command line, swept by <c>EveClientPresenceService</c>) — drives the green dot in the list.</summary>
    [ObservableProperty] private bool _hasActiveClient;

    public string ClientStatusTooltip => HasActiveClient
        ? "EVE client running on this PC"
        : "No running EVE client detected on this PC";

    partial void OnHasActiveClientChanged(bool value) => OnPropertyChanged(nameof(ClientStatusTooltip));

    /// <summary>Warning: the ESI token expired and could not be refreshed — the user must re-authenticate.</summary>
    [ObservableProperty] private bool _needsReauth;

    /// <summary>Public corp/alliance label ("Corp [TICK] · Alliance [TICK]"), kept fresh from public ESI; "—" until resolved.</summary>
    [ObservableProperty] private string _affiliation = "—";

    /// <summary>The ESI portrait render for the hex portrait, loaded best-effort; null → glyph fallback.</summary>
    [ObservableProperty] private Bitmap? _portrait;

    /// <summary>True once a portrait render has been loaded — drives the image-vs-glyph switch in the hex.</summary>
    public bool HasPortrait => Portrait is not null;

    /// <summary>First letter of the name, shown in the hex when no portrait render is available (offline/disabled).</summary>
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    partial void OnPortraitChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPortrait));

    /// <summary>Servers this character is coupled to, each with its own live bus state + decouple.</summary>
    public ObservableCollection<ServerLinkViewModel> ServerLinks { get; } = [];

    /// <summary>Synced: coupled to at least one server (Mode B).</summary>
    public bool IsSynced => ServerLinks.Count > 0;

    public string ScopeStatus
    {
        get
        {
            var read  = HasReadFittings  ? "✓ read" : "✗ read";
            var write = HasWriteFittings ? "✓ write" : "✗ write";
            return $"fittings: {read} · {write}";
        }
    }

    /// <summary>ESI-side indicator: local token + re-auth state.</summary>
    public string EsiStatus =>
        NeedsReauth ? "ESI: ⚠️ re-auth needed"
        : IsLocal   ? "ESI: 🏠 connected"
        :             "ESI: — not signed in";

    // --- The mockup-style ESI status chip for the character list (module-shell mockup "ESI" chip):
    // the chip text + the mutually exclusive style variant it renders in. Hover shows EsiStatus. ---

    public string EsiChipText => NeedsReauth ? "ESI ⚠" : IsLocal ? "ESI ✓" : "ESI —";

    /// <summary>Healthy accent chip: a working local ESI token.</summary>
    public bool EsiOk => IsLocal && !NeedsReauth;

    /// <summary>Amber chip: the token expired and needs a re-auth.</summary>
    public bool EsiWarn => NeedsReauth;

    /// <summary>Inert chip: not signed in at all (mutually exclusive with ok/warn, so the variants never stack).</summary>
    public bool EsiDim => !IsLocal && !NeedsReauth;

    /// <summary>The names of the implants this character has plugged in, shown as a badge + tooltip in the
    /// overview so it is clear at a glance which implants a character carries.</summary>
    public ObservableCollection<string> ImplantNames { get; } = [];
    public bool HasImplants => ImplantNames.Count > 0;
    public string ImplantBadge => $"💉 {ImplantNames.Count}";
    public string ImplantTooltip => ImplantNames.Count == 0 ? "No implants" : string.Join("\n", ImplantNames);

    /// <summary>Sets the character's plugged-in implants (resolved names), refreshing the overview badge + tooltip.</summary>
    public void SetImplants(IReadOnlyList<string> names)
    {
        ImplantNames.Clear();
        foreach (var name in names)
            ImplantNames.Add(name);
        OnPropertyChanged(nameof(HasImplants));
        OnPropertyChanged(nameof(ImplantBadge));
        OnPropertyChanged(nameof(ImplantTooltip));
    }

    public CharacterViewModel(Character character)
    {
        CharacterId = character.EsiCharacterId ?? 0;
        IsLocalOnly = character.EsiCharacterId is null;
        Name = character.Name;
        HasReadFittings  = character.HasScope(FittingsScopeCatalog.ReadFittings);
        HasWriteFittings = character.HasScope(FittingsScopeCatalog.WriteFittings);
        ServerLinks.CollectionChanged += OnServerLinksChanged;
    }

    // Each per-server icon (with its own tooltip) lives in the list row and updates itself from
    // ServerLinkViewModel; the character only needs to know whether it is coupled at all (IsSynced).
    private void OnServerLinksChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(IsSynced));

    partial void OnIsLocalChanged(bool value)
    {
        OnPropertyChanged(nameof(EsiStatus));
        OnPropertyChanged(nameof(EsiChipText));
        OnPropertyChanged(nameof(EsiOk));
        OnPropertyChanged(nameof(EsiWarn));
        OnPropertyChanged(nameof(EsiDim));
    }

    partial void OnNeedsReauthChanged(bool value)
    {
        OnPropertyChanged(nameof(EsiStatus));
        OnPropertyChanged(nameof(EsiChipText));
        OnPropertyChanged(nameof(EsiOk));
        OnPropertyChanged(nameof(EsiWarn));
        OnPropertyChanged(nameof(EsiDim));
    }
}
