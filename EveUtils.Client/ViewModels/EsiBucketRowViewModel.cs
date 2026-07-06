using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.ViewModels;

/// <summary>
/// One bucket row in the ESI-metrics window: a live snapshot of an <see cref="EsiBucketState"/> —
/// the call/error counters, both ESI rate-limit systems' remaining headroom, and the per-endpoint drill-down. The
/// bucket key is <c>app:characterId</c> for authed calls and <c>ip</c> for public ones. The instance persists
/// across the window's 2-second poll (<see cref="Update"/> refreshes its values in place) so the accordion's
/// expanded state survives a refresh instead of collapsing.
/// </summary>
public partial class EsiBucketRowViewModel : ObservableObject
{
    public string Key { get; }

    /// <summary>The character id for an authed (<c>app:</c>) bucket; <c>null</c> for the public <c>ip</c> bucket.</summary>
    public int? CharacterId { get; }

    /// <summary>True for an authed bucket — it gets a character portrait + name; false for the public ip bucket.</summary>
    public bool IsCharacter => CharacterId is not null;

    /// <summary>Row label: the resolved character name once known, else <c>character:{id}</c> (authed) or the key (ip).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initial))]
    private string _displayName = "";

    /// <summary>Hover detail: <c>{name}\ncharacter:{id}</c> for an authed bucket; a short note for the ip bucket.</summary>
    [ObservableProperty] private string _identityTooltip = "";

    /// <summary>The character's ESI portrait; null until loaded or when images are off/offline → initial-glyph fallback.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPortrait))]
    private Bitmap? _portrait;

    public bool HasPortrait => Portrait is not null;

    /// <summary>First letter shown in the hex when no portrait render is available (offline/disabled/not yet loaded).</summary>
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

    /// <summary>Accordion state, two-way bound to the Expander — persists across the metrics poll.</summary>
    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private long _calls;
    [ObservableProperty] private long _successes;
    [ObservableProperty] private long _failures;
    [ObservableProperty] private bool _hasFailures;
    [ObservableProperty] private string _errorRateText = "—";
    [ObservableProperty] private string _limitHitsText = "0/0";
    [ObservableProperty] private string _cacheText = "0/0";
    [ObservableProperty] private string _cacheTooltip = "";
    [ObservableProperty] private int _lastStatus;
    [ObservableProperty] private string _errorRemainingText = "—";
    [ObservableProperty] private string _bucketRemainingText = "—";
    [ObservableProperty] private string _updatedText = "—";
    [ObservableProperty] private string _summaryText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndpointCount))]
    private IReadOnlyList<EsiEndpointRowViewModel> _endpoints = [];

    public int EndpointCount => Endpoints.Count;

    public EsiBucketRowViewModel(EsiBucketState bucket)
    {
        Key = bucket.Key;
        if (Key.StartsWith("app:", StringComparison.Ordinal) && int.TryParse(Key.AsSpan(4), out var characterId))
        {
            CharacterId = characterId;
            _displayName = $"character:{characterId}";   // until the name resolves
            _identityTooltip = $"character:{characterId}";
        }
        else
        {
            _displayName = Key;
            _identityTooltip = Key == "ip" ? "Public calls — IP-based rate-limit bucket" : Key;
        }
        Update(bucket);
    }

    /// <summary>
    /// Applies the resolved character identity (best-effort, once per row): the name as the row label + a richer
    /// tooltip, and the portrait when image loading is on. A null name/portrait leaves the <c>character:{id}</c>
    /// fallback + initial glyph.
    /// </summary>
    public void ApplyCharacterIdentity(string? name, Bitmap? portrait)
    {
        if (!string.IsNullOrEmpty(name))
        {
            DisplayName = name;
            IdentityTooltip = $"{name}\ncharacter:{CharacterId}";
        }
        if (portrait is not null)
            Portrait = portrait;
    }

    /// <summary>Refreshes this row's values from the latest bucket state, leaving <see cref="IsExpanded"/> untouched.</summary>
    public void Update(EsiBucketState bucket)
    {
        Calls = bucket.Calls;
        Successes = bucket.Successes;
        Failures = bucket.Failures;
        HasFailures = bucket.Failures > 0;
        ErrorRateText = bucket.Calls == 0 ? "—" : $"{100.0 * bucket.Failures / bucket.Calls:0.#}%";
        LimitHitsText = $"{bucket.ErrorLimitHits}/{bucket.BucketHits}";
        CacheText = bucket.LocalCacheHits > 0
            ? $"{bucket.CacheHits}/{bucket.CacheMisses} · {bucket.LocalCacheHits}l"
            : $"{bucket.CacheHits}/{bucket.CacheMisses}";
        CacheTooltip =
            (bucket.CacheHits + bucket.CacheMisses == 0
                ? "no ESI CDN cache status seen yet"
                : $"{100.0 * bucket.CacheHits / (bucket.CacheHits + bucket.CacheMisses):0.#}% HIT (X-Esi-Cache-Status)")
            + (bucket.LocalCacheHits > 0 ? $" · {bucket.LocalCacheHits} served from local file cache (no network)" : "")
            + (string.IsNullOrEmpty(bucket.LastCacheStatus) ? "" : $" · last: {bucket.LastCacheStatus}");
        LastStatus = bucket.LastStatus;
        ErrorRemainingText = bucket.ErrorRemaining?.ToString() ?? "—";
        BucketRemainingText = bucket.BucketRemaining is { } remaining
            ? bucket.BucketLimit is { } limit ? $"{remaining}/{limit}" : remaining.ToString()
            : "—";
        UpdatedText = bucket.UpdatedAt == default ? "—" : bucket.UpdatedAt.LocalDateTime.ToString("HH:mm:ss");
        Endpoints = bucket.Endpoints.Values
            .OrderByDescending(e => e.Calls)
            .Select(e => new EsiEndpointRowViewModel(e))
            .ToList();
        SummaryText =
            $"{bucket.Calls} calls · " +
            (bucket.Calls == 0 ? "—" : $"{100.0 * bucket.Failures / bucket.Calls:0.#}%") + " err · " +
            $"err-left {bucket.ErrorRemaining?.ToString() ?? "—"} · " +
            $"{bucket.Endpoints.Count} endpoint" + (bucket.Endpoints.Count == 1 ? "" : "s");
    }
}
