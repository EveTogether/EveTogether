using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// FIT in the run window: the run's one fit (ET-107), filled from ET-101's detection or the reason it could not be.
/// Never a proposal standing beside a choice — a manual pick comes back through the same reading as its own match
/// reason.
/// </summary>
public sealed partial class FitWindowSectionViewModel(IRunWindowContext context)
    : RunWindowSection(context, RunSectionId.Fit, "FIT")
{
    private ShipFitDetectionReading? _fitReading;

    [ObservableProperty]
    private string _fitText = "no fit: the run has no character yet";

    /// <summary>Whether <see cref="FitText"/> names a fit. A state rather than a comparison against the text, so
    /// rewording a line can never silently flip what the window offers.</summary>
    [ObservableProperty] private bool _hasFit;

    [ObservableProperty] private string _fitVelocityText = "no max velocity";

    [ObservableProperty] private string _fitWarpSpeedText = "no warp speed";

    public override void RefreshSummary() => HeaderSummary = FitText;

    /// <summary>Clock-driven like the fleet command is, so starting a run fills the fit without the player
    /// confirming anything.</summary>
    public override void Refresh(DateTime nowUtc) => _ = RefreshFitAsync();

    /// <summary>Fill the run's fit from ET-101's reading. An unlinked fit comes back through the same reading, so it
    /// survives the window being closed and reopened mid-run.</summary>
    public async Task RefreshFitAsync()
    {
        if (Context.ActingCharacterId is not { } characterId
            || Context.Services.GetService<IShipFitDetectionService>() is not { } detection)
            return;

        ShipFitDetectionReading reading = detection.GetReading(characterId);
        if (ReferenceEquals(reading, _fitReading))
            return;

        _fitReading = reading;
        ApplyFitDetection(reading);
        await _LoadFitStatsAsync(reading.SelectedFit?.Id);
    }

    private async Task _LoadFitStatsAsync(int? fittingId)
    {
        if (fittingId is not { } id || Context.Services.GetService<IFittingRepository>() is not { } fittings)
        {
            ApplyFitStats(null, fitCouldBeRead: true);
            return;
        }

        LocalFitting? fitting = await fittings.FindByIdAsync(id);
        EsiFitting? esi = _ReadFitting(fitting?.RawJson);
        ApplyFitStats(
            esi is null ? null : await Context.Services.GetRequiredService<IFitStatsProvider>().ComputeAsync(esi),
            fitCouldBeRead: esi is not null);
    }

    private static EsiFitting? _ReadFitting(string? rawJson)
    {
        if (rawJson is null)
            return null;
        try { return JsonSerializer.Deserialize<EsiFitting>(rawJson); }
        catch (JsonException) { return null; }
    }

    [RelayCommand]
    private async Task ChooseFitAsync()
    {
        var dialogs = Context.Services.GetRequiredService<IDialogService>();
        if (Context.ActingCharacterId is not { } characterId)
        {
            await dialogs.ShowMessageAsync("Choose a fit",
                "Start the run first — its character is what a fit is filed under.");
            return;
        }

        var picker = new FitPickerViewModel(Context.Services, FitPickerMode.Single, alreadyAdded: null,
            composition: null, currentFitHash: null, skillCheckCharacterId: characterId);
        FitReferenceInfo? fit = await dialogs.PickFitAsync(picker);
        if (fit is null)
            return;
        if (fit.LocalFittingId is null)
        {
            await dialogs.ShowMessageAsync("Choose a local fit", "Only a local fit can be filed against a run.");
            return;
        }

        var detection = Context.Services.GetRequiredService<IShipFitDetectionService>();
        Result overrideResult = await detection.SetManualFitAsync(characterId, fit.LocalFittingId);
        if (!overrideResult.IsSuccess)
        {
            await dialogs.ShowMessageAsync("Fit selection",
                overrideResult.Messages.FirstOrDefault()?.Text ?? "Could not save the fit selection.");
            return;
        }

        _fitReading = null;
        await RefreshFitAsync();
    }

    /// <summary>Unlink: the run goes on without a fit. Stored by the detection service rather than held here, or
    /// closing and reopening the window mid-run would quietly fill back in what the player just took off.</summary>
    [RelayCommand]
    private async Task DetachFitAsync()
    {
        if (Context.ActingCharacterId is not { } characterId
            || Context.Services.GetService<IShipFitDetectionService>() is not { } detection)
            return;

        Result detached = await detection.DetachFitAsync(characterId);
        if (!detached.IsSuccess)
        {
            await Context.Services.GetRequiredService<IDialogService>().ShowMessageAsync("Fit selection",
                detached.Messages.FirstOrDefault()?.Text ?? "Could not unlink the fit.");
            return;
        }

        _fitReading = null;
        await RefreshFitAsync();
    }

    internal void ApplyFitDetection(ShipFitDetectionReading reading)
    {
        // The four detection states of ET-101 stay four: whether a character may look at all, has not looked yet,
        // looked and found nothing, or looked and found too much are different answers with different remedies.
        FitText = reading.State switch
        {
            ShipFitDetectionState.Unobserved => "no fit: ship type has not been read yet",
            ShipFitDetectionState.ScopeMissing => "no fit: ship-type scope is missing",
            ShipFitDetectionState.Observed when reading.MatchReason == ShipFitMatchReason.Detached =>
                "no fit: unlinked from this run",
            ShipFitDetectionState.Observed when reading.MatchReason == ShipFitMatchReason.NoFitFound =>
                "no fit: no known fit matches the observed ship",
            ShipFitDetectionState.Observed when reading.SelectedFit is { } fit =>
                $"fit: {fit.Name} ({_FitMatchReason(reading.MatchReason)})",
            ShipFitDetectionState.Observed => "no fit: no single fit matches the observed ship",
            _ => "no fit: ship fit is unavailable"
        };
        HasFit = reading is { State: ShipFitDetectionState.Observed, SelectedFit: not null };
        RefreshSummary();
    }

    internal void ApplyFitStats(FitStats? stats, bool fitCouldBeRead)
    {
        if (!fitCouldBeRead)
        {
            FitVelocityText = "fit could not be read";
            FitWarpSpeedText = "fit could not be read";
            return;
        }

        FitVelocityText = stats is null ? "no max velocity" : $"max velocity: {stats.MaxVelocity:N0} m/s";
        FitWarpSpeedText = stats is null ? "no warp speed" : $"warp speed: {stats.WarpSpeed:N2} AU/s";
    }

    private static string _FitMatchReason(ShipFitMatchReason? reason) => reason switch
    {
        ShipFitMatchReason.ShipName => "name matches the observed ship",
        ShipFitMatchReason.OnlyFitForShipType => "only known fit for this ship type",
        ShipFitMatchReason.Manual => "manual choice",
        _ => "automatic suggestion"
    };
}
