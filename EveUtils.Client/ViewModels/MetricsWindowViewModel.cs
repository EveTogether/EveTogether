using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Esi;
using EveUtils.Client.Gamelog;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels;

/// <summary>A selectable local character in the metrics window's "Show characters" bar.</summary>
public partial class MetricsCharacterOption : ObservableObject
{
    public string Name { get; }
    public int CharacterId { get; }

    [ObservableProperty] private bool _isSelected;

    public MetricsCharacterOption(string name, int characterId, bool isSelected)
    {
        Name = name;
        CharacterId = characterId;
        _isSelected = isSelected;
    }
}

/// <summary>
/// The per-character metrics window: tick a set of your local characters and see a live row each
/// (DPS graph + bounty + location + enemies + stats), with a running bounty total across the selection. Reads
/// live snapshots from <see cref="GamelogClientService"/> on a 1 Hz timer; affiliation is a best-effort public
/// ESI lookup. Non-modal so it keeps updating beside the main window.
/// </summary>
public partial class MetricsWindowViewModel : ViewModelBase, IDisposable
{
    private readonly GamelogClientService _gamelog;
    private readonly ICharacterInfoService _characterInfo;
    private readonly DispatcherTimer _timer;
    private int _tick;

    public ObservableCollection<MetricsCharacterOption> Available { get; } = [];
    public ObservableCollection<CharacterMetricsRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _bountyTotal = "0 ISK";
    [ObservableProperty] private string _minedTotal = "";

    public MetricsWindowViewModel(IServiceProvider services, IReadOnlyList<(string Name, int CharacterId)> characters, string? preselect)
    {
        _gamelog = services.GetRequiredService<GamelogClientService>();
        _characterInfo = services.GetRequiredService<ICharacterInfoService>();

        foreach (var (name, id) in characters)
        {
            var option = new MetricsCharacterOption(name, id, isSelected: string.Equals(name, preselect, StringComparison.OrdinalIgnoreCase));
            option.PropertyChanged += OnOptionChanged;
            Available.Add(option);
            if (option.IsSelected)
                AddRow(option);
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        RefreshSnapshots();
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MetricsCharacterOption.IsSelected) || sender is not MetricsCharacterOption option)
            return;

        if (option.IsSelected)
            AddRow(option);
        else
            RemoveRow(option);
        RefreshSnapshots();
    }

    private void AddRow(MetricsCharacterOption option)
    {
        if (Rows.Any(r => string.Equals(r.Character, option.Name, StringComparison.OrdinalIgnoreCase)))
            return;

        var row = new CharacterMetricsRowViewModel(option.Name, option.CharacterId);
        Rows.Add(row);
        _ = _gamelog.EnsureSeededAsync(option.Name); // show persisted bounty/mined right away
        if (option.CharacterId > 0)
            _ = LoadAffiliationAsync(row);
    }

    private void RemoveRow(MetricsCharacterOption option)
    {
        var row = Rows.FirstOrDefault(r => string.Equals(r.Character, option.Name, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
            Rows.Remove(row);
    }

    private async Task LoadAffiliationAsync(CharacterMetricsRowViewModel row)
    {
        var info = await _characterInfo.RefreshAsync(row.CharacterId);
        Dispatcher.UIThread.Post(() => row.SetAffiliation(info?.AffiliationLabel ?? "—"));
    }

    // DPS graph at ~30fps (smooth, demo-parity); textual fields + running totals at ~1 Hz (every 30 ticks).
    private void Tick()
    {
        foreach (var row in Rows)
            row.TickGraph(_gamelog.PeekSample(row.Character));

        if (_tick++ % 30 == 0)
            RefreshSnapshots();
    }

    private void RefreshSnapshots()
    {
        long bounty = 0, mined = 0;
        foreach (var row in Rows)
        {
            row.RefreshSnapshot(_gamelog.Snapshot(row.Character));
            bounty += row.BountyValue;
            mined += row.MinedValue;
        }
        BountyTotal = $"{bounty:N0} ISK";
        MinedTotal = mined == 0 ? "" : $"{mined:N0} units mined";
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var option in Available)
            option.PropertyChanged -= OnOptionChanged;
    }
}
