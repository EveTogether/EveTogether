using System;
using System.Collections.Generic;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Settings.Dtos;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// A section of the run window (ET-236). It sees the window only through <see cref="Context"/>, and the window reaches
/// it only through the hooks below — each a no-op unless the section has something to do at that moment, so a new
/// section overrides what it needs and nothing else.
///
/// A section lives as long as its window, not as long as its type claims it: a window whose type changes mid-run
/// keeps a section it no longer shows, with everything it collected, and every hook still reaches it. After each
/// lifecycle hook the window puts every section's summary right itself.
/// </summary>
public abstract class RunWindowSection : ActivitySection, IDisposable
{
    protected RunWindowSection(IRunWindowContext context, RunSectionId id, string title) : base(id, title)
    {
        Context = context;
        Context.PropertyChanged += _OnContextChanged;
    }

    protected IRunWindowContext Context { get; }

    /// <summary>The window is loading. <paramref name="settings"/> is what this client remembers, for a section that
    /// keeps a choice of its own between runs — null for a window with no store to read it from.</summary>
    public virtual void Load(IReadOnlyList<SettingDto>? settings)
    {
    }

    /// <summary>Put the shut header's line right. Called whenever the window works its own readout out again.</summary>
    public virtual void RefreshSummary()
    {
    }

    /// <summary>The window's clock ticked.</summary>
    public virtual void Refresh(DateTime nowUtc)
    {
    }

    /// <summary>The run on screen is on the clock — started, resumed, joined or adopted.</summary>
    public virtual void OnRunStarted()
    {
    }

    /// <summary>Another of the pilot's own toons got a run in this group (ET-210).</summary>
    public virtual void OnCharacterRunStarted(int characterId)
    {
    }

    /// <summary>The run is committed or thrown away: whatever the section was still collecting for it is let go.</summary>
    public virtual void OnRunClosed()
    {
    }

    /// <summary>Add what this section holds for one run to what SAVE stores for it.</summary>
    public virtual void AddToSave(RunSaveDraft draft)
    {
    }

    /// <summary>How much of this section's own ISK-shaped reward data must not count towards TOTAL ISK even though
    /// it is still among the summed parameters — a mission's bonus once its time window has passed (ET-237). TOTAL
    /// ISK stays the general reward sum every parameter feeds (<c>TotalIskCalculator.RewardIsk</c>); this is only
    /// ever subtracted from it, never a replacement for it, so a type with no such rule needs no override.</summary>
    public virtual decimal ExpiredBonusIsk => 0m;

    /// <summary>One of <see cref="Context"/>'s properties changed; re-announce whatever this section shows from it.</summary>
    protected virtual void OnContextChanged(string? propertyName)
    {
    }

    public virtual void Dispose() => Context.PropertyChanged -= _OnContextChanged;

    private void _OnContextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnContextChanged(e.PropertyName);
}
