using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using UiDispatcher = Avalonia.Threading.Dispatcher;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Formatting;
using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-478: the compact run window — a card and a one-line HUD, read from the very view model the full window uses.
/// Headless; the figures are asserted on the view model and the shape on <c>.Bounds</c>.
/// </summary>
public sealed class CompactRunWindowTests
{
    private const int ErvekamId = 30003867;
    private static readonly SdeSite RelayOutpost =
        new(2406, "Command Relay Outpost", null, "Escalation", 500019, "Sansha's Nation", null, 3, false, []);

    // ── The settings ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Settings_WithNothingStored_AreCardFullAndNotOpeningCompact()
    {
        Assert.Equal(CompactRunStyle.Card, CompactRunSettings.ReadStyle([]));
        Assert.False(CompactRunSettings.ReadOpenCompact([]));
        Assert.False(CompactRunSettings.ReadCompact([]));
        Assert.False(CompactRunSettings.StartsCompact(null));
    }

    [Fact]
    public void Settings_RoundTripAllThreeKeys()
    {
        IReadOnlyList<SettingDto> stored =
        [
            new(0, CompactRunSettings.StyleKey, nameof(CompactRunStyle.Hud)),
            new(0, CompactRunSettings.OpenCompactKey, "true"),
            new(0, CompactRunSettings.CompactKey, "true")
        ];

        Assert.Equal(CompactRunStyle.Hud, CompactRunSettings.ReadStyle(stored));
        Assert.True(CompactRunSettings.ReadOpenCompact(stored));
        Assert.True(CompactRunSettings.ReadCompact(stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Strip")]
    [InlineData("7")]
    public void Settings_AStyleNobodyKnows_FallsBackToCard(string stored) =>
        Assert.Equal(CompactRunStyle.Card, CompactRunSettings.ReadStyle([new(0, CompactRunSettings.StyleKey, stored)]));

    [AvaloniaFact]
    public async Task SettingsWindow_ShowsTheStoredChoices_AndHandsBackWhatWasChosen()
    {
        SettingsResult? applied = null;
        var window = new SettingsWindow(
            currentDirectory: "", detectedDefault: "",
            shareLocation: false, shareBounty: false, shareCombat: true, loadTypeImages: false,
            currentFaction: EveUtils.Client.Theming.FactionTheme.Gallente, sdeVersionLabel: "",
            onApply: result =>
            {
                applied = result;
                return Task.CompletedTask;
            },
            compactRunStyle: CompactRunStyle.Hud, openRunsCompact: true);
        window.Show();

        Assert.True(window.FindControl<RadioButton>("CompactStyleHudRadio")?.IsChecked);
        Assert.True(window.FindControl<CheckBox>("OpenRunsCompactBox")?.IsChecked);

        window.FindControl<RadioButton>("CompactStyleCardRadio")!.IsChecked = true;
        window.FindControl<CheckBox>("OpenRunsCompactBox")!.IsChecked = false;
        window.FindControl<Button>("SaveButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ActivityWindowHarness.WaitUntil(() => applied is not null);

        Assert.Equal((CompactRunStyle.Card, false), (applied!.CompactRunStyle, applied.OpenRunsCompact));
        window.Close();
    }

    // ── The toggle, and what is remembered ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task ANewWindow_IsFull_AndCard_ByDefault()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();

        ActivityWindowViewModel model = await harness.OpenAsync();

        Assert.False(model.IsCompact);
        Assert.Equal(CompactRunStyle.Card, model.CompactStyle);
        Assert.True(model.IsFullShown);
        Assert.False(model.IsCardShown);
        Assert.False(model.IsHudShown);
    }

    [AvaloniaFact]
    public async Task TheToggle_SwitchesBetweenFullAndCompact_WithoutLosingTheRun()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureName = "Sansha Hideaway";
        await model.StartRunCommand.ExecuteAsync(null);
        Guid? runId = model.RunId;

        await model.ToggleCompactCommand.ExecuteAsync(null);

        Assert.True(model.IsCompact);
        Assert.True(model.IsCardShown);
        Assert.False(model.IsFullShown);
        Assert.Equal((runId, ActivityRunState.Running), (model.RunId, model.RunState));

        await model.ToggleCompactCommand.ExecuteAsync(null);

        Assert.False(model.IsCompact);
        Assert.True(model.IsFullShown);
        Assert.Equal((runId, ActivityRunState.Running), (model.RunId, model.RunState));
    }

    [AvaloniaFact]
    public async Task TheLastState_IsRememberedByTheNextWindow_AndByARestartAfterGoingBackToFull()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel first = await harness.OpenAsync();
        await first.ToggleCompactCommand.ExecuteAsync(null);

        ActivityWindowViewModel second = await harness.OpenAsync();

        Assert.True(second.IsCompact);

        await second.ToggleCompactCommand.ExecuteAsync(null);
        ActivityWindowViewModel third = await harness.OpenAsync();

        Assert.False(third.IsCompact);
        Assert.Equal("false", await _StoredAsync(harness, CompactRunSettings.CompactKey));
    }

    [AvaloniaFact]
    public async Task OpenRunsInCompactView_OpensCompact_EvenAfterTheLastWindowWasLeftFull()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        await _SetAsync(harness, CompactRunSettings.OpenCompactKey, "true");
        await _SetAsync(harness, CompactRunSettings.CompactKey, "false");

        ActivityWindowViewModel model = await harness.OpenAsync();

        Assert.True(model.IsCompact);
    }

    [AvaloniaFact]
    public async Task TheStylePreference_ChoosesTheView_AndChangesAnOpenCompactWindowAtOnce()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        await _SetAsync(harness, CompactRunSettings.StyleKey, nameof(CompactRunStyle.Hud));
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ToggleCompactCommand.ExecuteAsync(null);

        Assert.True(model.IsHudShown);
        Assert.False(model.IsCardShown);

        model.UseCompactStyle(CompactRunStyle.Card);

        Assert.True(model.IsCardShown);
        Assert.False(model.IsHudShown);
    }

    [AvaloniaFact]
    public async Task LoadingAgain_DoesNotUndoATogglePilotMadeSince()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        await _SetAsync(harness, CompactRunSettings.OpenCompactKey, "true");
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ToggleCompactCommand.ExecuteAsync(null);
        Assert.False(model.IsCompact);

        await model.LoadAsync();

        Assert.False(model.IsCompact);
    }

    // ── One calculation: the compact figures are the full view's ───────────────────────────────────

    [AvaloniaFact]
    public async Task CompactBounty_IsTheSameFigureTheBountySectionHolds()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await harness.WriteLineAsync(ActivityWindowHarness.BountyLine("67.500"));
        await ActivityWindowHarness.WaitUntil(() => model.BountyIsk > 0);

        await model.ToggleCompactCommand.ExecuteAsync(null);
        model.Refresh(DateTime.UtcNow);

        Assert.Equal(IskFormat.Whole(model.BountyIsk), model.CompactBountyText);
        Assert.Equal(67_500, model.BountyIsk);
    }

    [AvaloniaFact]
    public async Task TheCardAndTheHud_ShowTheWindowsOwnClockAndTotal()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();

        TextBlock cardClock = _Named<TextBlock>(window, "CompactClock");
        TextBlock cardTotal = _Named<TextBlock>(window, "CompactTotal");
        Assert.Equal(model.ClockText, cardClock.Text);
        Assert.Equal(model.GroupTotalIskText, cardTotal.Text);

        model.UseCompactStyle(CompactRunStyle.Hud);
        UiDispatcher.UIThread.RunJobs();

        Assert.Equal(model.ClockText, _Named<TextBlock>(window, "HudClock").Text);
        Assert.Equal(model.GroupTotalIskText, _Named<TextBlock>(window, "HudTotal").Text);
        Assert.Equal(model.CompactLootText, _Named<TextBlock>(window, "HudLoot").Text);
        Assert.Equal(model.CompactBountyText, _Named<TextBlock>(window, "HudBounty").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheClock_TurnsAmberAndRedInCompactLikeInTheFullWindow()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        TextBlock clock = _Named<TextBlock>(window, "CompactClock");

        model.IsClockWarning = true;
        UiDispatcher.UIThread.RunJobs();
        Assert.Contains("warn", clock.Classes);

        model.IsClockWarning = false;
        model.IsClockCritical = true;
        UiDispatcher.UIThread.RunJobs();
        Assert.Contains("crit", clock.Classes);
        window.Close();
    }

    // ── The buttons are the window's own ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task TheRunButtons_AreTheFullWindowsCommands_AndFollowTheSameFlags()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();

        Button save = _Named<Button>(window, "CompactSaveButton");
        Button discard = _Named<Button>(window, "CompactDiscardButton");
        Button stop = _Named<Button>(window, "CompactStopButton");
        Button start = _Named<Button>(window, "CompactStartButton");
        Assert.Same(model.SaveRunCommand, save.Command);
        Assert.Same(model.DiscardRunCommand, discard.Command);
        Assert.Same(model.StopRunCommand, stop.Command);
        Assert.Same(model.StartRunCommand, start.Command);
        Assert.Equal((model.IsSaveButtonVisible, model.IsDiscardButtonVisible, model.IsStopButtonVisible, model.IsStartButtonVisible),
            (save.IsVisible, discard.IsVisible, stop.IsVisible, start.IsVisible));
        Assert.True(stop.IsVisible);
        Assert.False(start.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Discard_FromCompact_StillAsksBeforeEndingTheRun()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        harness.Dialogs.OnConfirm = (_, _) => Task.FromResult(false);

        await model.DiscardRunCommand.ExecuteAsync(null);

        Assert.Single(harness.Dialogs.ConfirmPrompts);
        Assert.Same(model, Assert.Single(harness.Dialogs.ConfirmOwners));   // the dialog is owned by the same window
        Assert.Equal(ActivityRunState.Running, model.RunState);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EveryButton_IsOffWhileTheRunIsSaving()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        var window = new ActivityWindow(model);
        window.Show();
        model.IsSaving = true;
        UiDispatcher.UIThread.RunJobs();

        Assert.All(new[] { "CompactStopButton", "CompactSaveButton", "CompactDiscardButton" },
            name => Assert.False(_Named<Button>(window, name).IsEnabled));
        window.Close();
    }

    // ── What is only there while it applies ────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task TierAndWeather_IsAChipOnlyWhileItIsNotFilledIn_AndUnfoldsTheTwoPickers()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        model.Refresh(DateTime.UtcNow);

        Assert.True(model.NeedsWeatherAndTier);
        Assert.True(model.HasCompactNoticeContent);

        model.ToggleNoticeCommand.Execute(CompactNotice.TierWeather);
        Assert.True(model.IsTierNoticeOpen);

        model.ToggleNoticeCommand.Execute(CompactNotice.OpenEscalation);
        Assert.False(model.IsTierNoticeOpen);
        Assert.True(model.IsAlertNoticeOpen);   // one at a time
        model.ToggleNoticeCommand.Execute(CompactNotice.TierWeather);

        await model.CompactActivity!.SelectWeatherCommand.ExecuteAsync(0);
        await model.CompactActivity.SelectTierCommand.ExecuteAsync(0);
        model.Refresh(DateTime.UtcNow);

        Assert.False(model.NeedsWeatherAndTier);
        Assert.Equal(CompactNotice.None, model.ExpandedNotice);   // folds itself with the situation
        Assert.False(model.HasCompactNoticeContent);
    }

    [AvaloniaFact]
    public async Task ASiteRun_HasNoTierChip_AndNoNoticeWhenNothingApplies()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        model.Refresh(DateTime.UtcNow);

        Assert.False(model.NeedsWeatherAndTier);
        Assert.False(model.HasCompactAlert);
        Assert.False(model.HasCompactPricingNotice);
        Assert.False(model.HasCompactNotice);
        Assert.Equal("—", model.CompactLootText);
    }

    [AvaloniaFact]
    public async Task LootWithoutAPrice_ShowsTheChip_AFloorMark_AndTheTopItems()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        await harness.Services.GetRequiredService<CqrsDispatcher>().Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = DateTime.UtcNow,
            Source = LootCaptureSource.Clipboard,
            ContentHash = "hash-" + Guid.NewGuid().ToString("N"),
            Entries =
            [
                new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 100, Volume = 1, ClipboardPrice = 5, LootKind = LootKind.Gained }
            ],
            UnrecognisedNames = [new UnrecognisedLootNameInput { Name = "Crimson Harvest Token", Quantity = 3 }]
        }));

        await ActivityWindowHarness.WaitUntil(() => model.LootOverview?.UnrecognisedCount > 0);
        model.Refresh(DateTime.UtcNow);

        Assert.True(model.HasCompactPricingNotice);
        Assert.Contains("1 UNRECOGNISED", model.CompactPricingChipText);
        Assert.StartsWith("≥ ", model.CompactLootText);
        Assert.Equal("Tritanium", Assert.Single(model.CompactTopLoot).Name);
        Assert.Equal(model.LootOverview!.LinesWithoutPriceText, model.CompactPricingDetailText);
        Assert.Equal(model.LootOverview.UnrecognisedText, model.CompactUnrecognisedDetailText);

        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        var hover = Assert.IsType<StackPanel>(ToolTip.GetTip(_Named<Grid>(window, "CompactLootRow")));
        Assert.Same(model, hover.DataContext);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheFollowCountdown_ReplacesTheButtons_WithTheFullWindowsOwnCommands()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        Assert.False(_Named<Grid>(window, "CompactFollow").IsVisible);
        Assert.True(_Named<StackPanel>(window, "CompactControls").IsVisible);

        model.FollowText = "The commander saved this run.";
        UiDispatcher.UIThread.RunJobs();

        Assert.True(_Named<Grid>(window, "CompactFollow").IsVisible);
        Assert.False(_Named<StackPanel>(window, "CompactControls").IsVisible);
        Assert.Same(model.FollowNowCommand, _Named<Button>(window, "CompactFollowNow").Command);
        Assert.Same(model.CancelFollowCommand, _Named<Button>(window, "CompactFollowCancel").Command);
        window.Close();
    }

    // ── The escalation of this run ──────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task AnEscalationRegisteredOnTheRun_IsAChip_ThatUnfoldsSiteSystemAndDeadline_Masked()
    {
        using var harness = await _CreateEscalationHarnessAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        model.Refresh(DateTime.UtcNow);
        Assert.False(model.HasCompactAlert);

        await _RegisterEscalationAsync(harness, model);
        model.Refresh(DateTime.UtcNow);

        Assert.True(model.HasCompactAlert);
        Assert.StartsWith("ESCALATION · expires in", model.CompactAlertChipText);
        model.ToggleNoticeCommand.Execute(CompactNotice.OpenEscalation);
        Assert.True(model.IsAlertNoticeOpen);
        Assert.True(OpsecText.IsMarked(model.CompactAlertWhereText));
        Assert.Equal("Command Relay Outpost · Ervekam", OpsecText.Strip(model.CompactAlertWhereText));
        Assert.True(model.CanStartCompactAlert);
    }

    [AvaloniaFact]
    public async Task StartingTheEscalationFromCompact_IsRefusedLikeTheBandDoes_WhileThisRunIsStillOpen()
    {
        using var harness = await _CreateEscalationHarnessAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);
        await _RegisterEscalationAsync(harness, model);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        model.Refresh(DateTime.UtcNow);
        Assert.True(model.CanStartCompactAlert);

        await model.StartCompactAlertCommand.ExecuteAsync(null);

        Assert.Contains("already flying a run", model.CompactAlertMessage);
        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        Assert.Equal(1, await db.Set<Run>().CountAsync());
    }

    // ── OPSEC ───────────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task CompactWhereText_MarksSiteAndSystem_SoOpsecCanMaskThem()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureName = "Sansha Hideaway";
        model.SolarSystem = "Ervekam";
        await model.ToggleCompactCommand.ExecuteAsync(null);

        Assert.True(OpsecText.IsMarked(model.CompactWhereText));
        Assert.Equal("Sansha Hideaway · Ervekam", OpsecText.Strip(model.CompactWhereText));
        Assert.Equal($"{OpsecText.Mark("Sansha Hideaway")} · {OpsecText.Mark("Ervekam")}", model.CompactWhereText);
    }

    [AvaloniaFact]
    public async Task WithOpsecOn_TheCardAndTheHudDrawNoSite()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureName = "Sansha Hideaway";
        model.SolarSystem = "Ervekam";
        await model.ToggleCompactCommand.ExecuteAsync(null);
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        TextBlock card = _Named<TextBlock>(window, "CompactWhere");
        Assert.StartsWith("Sansha Hideaway", card.Text);

        using (TestOpsec.On())
        {
            model.SignatureName = "Sansha Refuge";
            model.Refresh(DateTime.UtcNow);
            UiDispatcher.UIThread.RunJobs();

            Assert.DoesNotContain("Sansha", card.Text);

            model.UseCompactStyle(CompactRunStyle.Hud);
            UiDispatcher.UIThread.RunJobs();
            string tip = Assert.IsType<string>(ToolTip.GetTip(_Named<TextBlock>(window, "HudWhereLabel")));
            Assert.True(OpsecText.IsMarked(tip));   // marked, so it is masked wherever it is drawn
            Assert.DoesNotContain("Sansha", _Named<TextBlock>(window, "HudWhereLabel").Text);
        }

        window.Close();
    }

    // ── Who is on the run ───────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Solo_ShowsOnlyTheFlyingAccount_AndAFleetShowsThreeHexesAndTheRestAsAMore()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ToggleCompactCommand.ExecuteAsync(null);
        model.Refresh(DateTime.UtcNow);

        Assert.False(model.IsCompactFleet);
        Assert.Equal(ActivityWindowHarness.CharacterName, model.CompactSoloName);

        foreach (string name in new[] { "Kael Voss", "Mira Tal", "Dren Oku", "Sova Ihr", "Tess Vane" })
            model.Participants.Add(new RunParticipantViewModel(Guid.NewGuid(), name.GetHashCode(), name));
        model.Refresh(DateTime.UtcNow);

        Assert.True(model.IsCompactFleet);
        Assert.Equal(3, model.CompactMembers.Count);
        Assert.Equal("KV", model.CompactMembers[0].Initials);
        Assert.Equal("+2", model.CompactMoreMembersText);
        Assert.Equal(5, model.CompactRosterText.Split('\n').Length);
    }

    // ── The window ──────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task CompactTakesItsOwnWidth_AndFullComesBackWithTheSizeItHad()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        Size full = window.Bounds.Size;

        await model.ToggleCompactCommand.ExecuteAsync(null);
        UiDispatcher.UIThread.RunJobs();

        Assert.Equal(360, window.Bounds.Width, 1);
        Assert.InRange(window.Bounds.Height, 130, 170);
        Assert.False(window.CanResize);

        model.UseCompactStyle(CompactRunStyle.Hud);
        UiDispatcher.UIThread.RunJobs();

        Assert.Equal(560, window.Bounds.Width, 1);
        Assert.InRange(window.Bounds.Height, 32, 40);

        await model.ToggleCompactCommand.ExecuteAsync(null);
        UiDispatcher.UIThread.RunJobs();

        Assert.Equal(full.Width, window.Bounds.Width, 1);
        Assert.Equal(full.Height, window.Bounds.Height, 1);
        Assert.True(window.CanResize);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheRightEdge_StaysPut_WhenSwitchingAndWhenANoticeUnfolds()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        double RightEdge() => window.Position.X + window.Bounds.Width * window.RenderScaling;
        double top = window.Position.Y;
        double before = RightEdge();

        await model.ToggleCompactCommand.ExecuteAsync(null);
        UiDispatcher.UIThread.RunJobs();
        double compactHeight = window.Bounds.Height;
        Assert.Equal(before, RightEdge(), 1);

        model.Refresh(DateTime.UtcNow);
        model.ToggleNoticeCommand.Execute(CompactNotice.TierWeather);
        UiDispatcher.UIThread.RunJobs();
        Assert.True(window.Bounds.Height > compactHeight);   // it grows by a row...
        Assert.Equal(before, RightEdge(), 1);                // ...and stays on its right edge
        Assert.Equal(top, window.Position.Y);

        await model.ToggleCompactCommand.ExecuteAsync(null);
        UiDispatcher.UIThread.RunJobs();
        Assert.Equal(before, RightEdge(), 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pin_AndOpacity_AreNotTouchedByTheSwitch()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        var window = new ActivityWindow(model);
        window.Show();
        window.Topmost = false;
        window.FillOpacity = 0.4;
        UiDispatcher.UIThread.RunJobs();

        await model.ToggleCompactCommand.ExecuteAsync(null);
        UiDispatcher.UIThread.RunJobs();
        Assert.False(window.Topmost);
        Assert.Equal(0.4, window.FillOpacity);

        model.UseCompactStyle(CompactRunStyle.Hud);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        UiDispatcher.UIThread.RunJobs();
        Assert.False(window.Topmost);
        Assert.Equal(0.4, window.FillOpacity);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Compact_HasNoSections_AndNoCharacterColumn()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        Assert.True(_Named<ItemsControl>(window, "SectionList").IsEffectivelyVisible);

        await model.ToggleCompactCommand.ExecuteAsync(null);
        UiDispatcher.UIThread.RunJobs();

        Assert.False(_Named<ItemsControl>(window, "SectionList").IsEffectivelyVisible);
        Assert.False(_Named<ItemsControl>(window, "RunCharacterColumn").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheToggleButton_IsOnTheFullHeaderAndOnBothCompactViews_NextToTheOtherThree()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        var window = new ActivityWindow(model);
        window.Show();
        UiDispatcher.UIThread.RunJobs();

        Assert.Single(_Descendants<OverlayChromeButtons>(window), chrome => chrome.IsEffectivelyVisible && chrome.ShowCompactToggle);
        await model.ToggleCompactCommand.ExecuteAsync(null);
        UiDispatcher.UIThread.RunJobs();
        OverlayChromeButtons card = Assert.Single(_Descendants<OverlayChromeButtons>(window), chrome => chrome.IsEffectivelyVisible);
        Assert.True(card.IsCompact);
        Assert.Equal("Full view", card.CompactToggleTip);
        Button toggle = _Named<Button>(card, "CompactToggleButton");
        Assert.Same(model.ToggleCompactCommand, toggle.Command);
        Assert.True(toggle.Bounds.Right <= _Named<Button>(card, "CloseButton").Bounds.Left);
        window.Close();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static T _Named<T>(Control root, string name) where T : Control =>
        _Descendants<T>(root).FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"{name} was not rendered");

    private static IEnumerable<T> _Descendants<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>();

    private static async Task _SetAsync(ActivityWindowHarness harness, string key, string value) =>
        await harness.Services.GetRequiredService<CqrsDispatcher>().Send(new SetSettingCommand(key, value));

    private static async Task<string?> _StoredAsync(ActivityWindowHarness harness, string key) =>
        (await harness.Services.GetRequiredService<CqrsDispatcher>().Query(new EveUtils.Shared.Modules.Settings.Queries.GetSettingsQuery()))
        .FirstOrDefault(setting => setting.Key == key)?.Value;

    private static Task<ActivityWindowHarness> _CreateEscalationHarnessAsync() =>
        ActivityWindowHarness.CreateAsync(configure: services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .AddSolarSystem(new SdeSolarSystem(ErvekamId, "Ervekam", 0.69))
            .AddSite(RelayOutpost)));

    private static async Task _RegisterEscalationAsync(ActivityWindowHarness harness, ActivityWindowViewModel model)
    {
        harness.Dialogs.OnShowEscalationDialog = dialog =>
        {
            dialog.SiteQuery = RelayOutpost.Name;
            dialog.SelectedOption = Assert.Single(dialog.SiteResults);
            dialog.DestinationSystem = "Ervekam";
            dialog.RemainingTimeText = "23:00:00";
            dialog.RegisterCommand.Execute(null);
            return Task.FromResult(true);
        };
        await model.Activity().RegisterEscalationCommand.ExecuteAsync(null);
    }
}
