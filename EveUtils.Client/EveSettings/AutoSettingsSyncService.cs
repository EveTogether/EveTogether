using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.EveSettings;

/// <summary>Why a pass did nothing, or what it did — the automatic sync's whole decision in one value, so a test can
/// pin down each branch and the log can say which one it took.</summary>
public enum AutoSyncPass
{
    /// <summary>Switched off, or nothing configured yet. The default state.</summary>
    Idle,

    /// <summary>An EVE client is running. Nothing is read or written while one is.</summary>
    ClientsRunning,

    /// <summary>The clients only just closed; EVE is still flushing its settings. Waiting one more turn.</summary>
    Settling,

    /// <summary>The source and the targets already hold the same bytes — nothing to do, and so no backup either.</summary>
    UpToDate,

    /// <summary>Files were copied.</summary>
    Synced,

    /// <summary>The instruction could not be carried out — the profile or the source is gone, or a write failed.</summary>
    Failed
}

/// <summary>
/// Keeps the settings in step on its own (ET-60): when every EVE client is closed, it copies the source the user
/// picked over the targets they picked, having backed the whole profile up first — exactly the call the buttons in
/// the tool make, made by nobody.
///
/// The one condition that makes this safe at all is that no EVE client is running. EVE writes its settings back out
/// when it closes, so copying into a running client is undone at logout, or lands half-written. That is checked on
/// the running process rather than on who is visibly in-game (a client on the login screen appears in no game log and
/// still rewrites everything on exit), it is checked again immediately before each file, and it is given
/// <see cref="SettleDelay"/> of quiet first so EVE has finished writing before we read what it wrote.
///
/// Three more things keep an unattended tool honest: it does nothing when nothing changed, so the disk does not fill
/// with identical snapshots; it prunes only the backups it made itself, never one the user asked for; and every run
/// that touched a file is written to the history and announced, because a silent automaton moving files is not
/// something anybody should have to discover.
/// </summary>
public sealed class AutoSettingsSyncService : BackgroundService
{
    /// <summary>How often the condition is looked at. Cheap: a process count, and the settings row.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long every client has to have been closed before anything is touched. EVE flushes its settings
    /// during shutdown, so acting the instant the process disappears risks copying a half-written file.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(30);

    /// <summary>How many automatic backups are kept. Enough to walk back several days of runs; the newest is never
    /// pruned, and backups the user made by hand or that came before a copy they pressed are never touched.</summary>
    public const int KeepAutomaticBackups = 10;

    private readonly SettingsSyncService _sync;
    private readonly SettingsBackupService _backups;
    private readonly EveSettingsNameResolver _names;
    private readonly EveSettingsPreferences _preferences;
    private readonly EveClientPresenceService? _presence;
    private readonly ILogger<AutoSettingsSyncService>? _logger;
    private readonly IToastService? _toasts;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _settleDelay;

    // Startup counts as "a client was just seen": EVE Together often starts right after the game did, and the first
    // pass should wait out the settle window rather than pounce on files EVE may still be writing.
    private DateTimeOffset _clientsLastSeen;
    private string? _lastFailure;

    public AutoSettingsSyncService(
        SettingsSyncService sync,
        SettingsBackupService backups,
        EveSettingsNameResolver names,
        EveSettingsPreferences preferences,
        EveClientPresenceService? presence = null,
        ILogger<AutoSettingsSyncService>? logger = null,
        IToastService? toasts = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? settleDelay = null)
    {
        _sync = sync;
        _backups = backups;
        _names = names;
        _preferences = preferences;
        _presence = presence;
        _logger = logger;
        _toasts = toasts;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _settleDelay = settleDelay ?? SettleDelay;
        _clientsLastSeen = _now();
    }

    /// <summary>Raised after a pass that actually copied something, off the UI thread — an open tool refreshes its
    /// file times and its backup list from it.</summary>
    public event Action<AutoSyncRun>? Ran;

    /// <summary>What the last pass decided, for the tool's status line.</summary>
    public AutoSyncPass LastPass { get; private set; } = AutoSyncPass.Idle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error in the automatic EVE settings sync.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One pass of the whole decision. Public so a test drives it without the timer, the way
    /// <see cref="EveClientPresenceService.PollOnce"/> is.</summary>
    public async Task<AutoSyncPass> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // Before anything else, and whether or not this is switched on: the settle window has to start when the last
        // client closed, not when the user got round to enabling this. Otherwise switching it on just after quitting
        // EVE would act on files EVE is still writing.
        var clientsRunning = _ClientsRunning();
        if (clientsRunning)
            _clientsLastSeen = _now();

        var settings = await _preferences.LoadAutoSyncAsync(cancellationToken);
        if (!settings.Enabled || !settings.IsConfigured)
            return _Pass(AutoSyncPass.Idle);

        if (clientsRunning)
            return _Pass(AutoSyncPass.ClientsRunning);

        if (_now() - _clientsLastSeen < _settleDelay)
            return _Pass(AutoSyncPass.Settling);

        var profileDirectory = Path.Combine(settings.InstallRoot, settings.ProfileName);
        if (!Directory.Exists(profileDirectory))
            return await _FailAsync($"the profile folder {profileDirectory} is gone", cancellationToken);

        var profile = EveSettingsLocator.LoadProfile(profileDirectory);
        var plans = new List<SettingsSyncPlan>();
        var missing = new List<string>();

        _AddPlan(plans, missing, settings, profile, SettingsFileKind.Character);
        _AddPlan(plans, missing, settings, profile, SettingsFileKind.Account);

        if (missing.Count > 0)
            return await _FailAsync(string.Join("; ", missing), cancellationToken);

        if (plans.Count == 0)
            return _Pass(AutoSyncPass.UpToDate);

        // Only now are the names worth resolving — a pass that found nothing to do should not go near the registry
        // or the ESI cache. The plans were built with file names standing in; they carry real ones from here on.
        var names = await _names.ResolveAsync(
            profile, EveSettingsLocator.LoadProfiles(settings.InstallRoot), cancellationToken);
        plans = plans.Select(plan => plan with
        {
            SourceName = names.DisplayName(plan.Source),
            TargetNames = plan.Targets.Select(names.DisplayName).ToList()
        }).ToList();

        var note = "automatic sync — " + string.Join("; ", plans.Select(plan =>
            $"{plan.SourceName} → {string.Join(", ", plan.TargetNames)}"));

        var outcome = _sync.ApplyAll(plans, names.AsLookup(), BackupReason.BeforeAutoSync, note,
            abortWhen: _ClientsRunning);
        if (!outcome.IsSuccess || outcome.Value is null)
            return await _FailAsync(string.Join(" ", outcome.Messages.Select(message => message.Text)), cancellationToken);

        _backups.Prune(KeepAutomaticBackups, BackupReason.BeforeAutoSync);

        var value = outcome.Value;
        var summary = value.Aborted
            ? $"Stopped part-way — an EVE client started. Copied to {string.Join(", ", value.Copied)}."
            : $"Copied {string.Join("; ", plans.Select(plan => $"{plan.SourceName} → {string.Join(", ", plan.TargetNames)}"))}.";
        if (value.Failed.Count > 0 && !value.Aborted)
            summary += $" Not copied: {string.Join("; ", value.Failed)}.";

        var run = new AutoSyncRun(_now(), summary, value.Backup.Id, value.Failed.Count > 0);
        await _preferences.AppendAutoSyncRunAsync(run, cancellationToken);
        _lastFailure = null;

        _logger?.LogInformation("Automatic EVE settings sync: {Summary} Backup {BackupId}.", summary, value.Backup.Id);
        _toasts?.Show("EVE settings synced", summary,
            value.Failed.Count > 0 ? ToastKind.Warning : ToastKind.Success);
        Ran?.Invoke(run);

        return _Pass(value.Failed.Count > 0 ? AutoSyncPass.Failed : AutoSyncPass.Synced);
    }

    /// <summary>Adds the rule for one kind, limited to the targets that are actually different. A target already
    /// holding the source's bytes is not copied to — that is what keeps an idle machine from producing a backup an
    /// hour.</summary>
    private static void _AddPlan(
        List<SettingsSyncPlan> plans,
        List<string> missing,
        AutoSyncSettings settings,
        EveSettingsProfile profile,
        SettingsFileKind kind)
    {
        var character = kind == SettingsFileKind.Character;
        var sourceId = character ? settings.CharacterSourceId : settings.AccountSourceId;
        var targetIds = character ? settings.CharacterTargetIds : settings.AccountTargetIds;
        if (sourceId is null || targetIds.Count == 0)
            return;

        var files = character ? profile.Characters : profile.Accounts;
        var source = files.FirstOrDefault(file => file.Id == sourceId);
        if (source is null)
        {
            missing.Add($"the {(character ? "character" : "account")} to copy from ({sourceId}) is no longer in {profile.Name}");
            return;
        }

        var targets = targetIds
            .Select(id => files.FirstOrDefault(file => file.Id == id))
            .OfType<EveSettingsFile>()
            .Where(file => file.Id != source.Id)
            .ToList();
        if (targets.Count == 0)
            return;   // the targets are gone from the profile; nothing to do rather than something to shout about

        var outOfSync = SettingsSyncService.OutOfSync(source, targets);
        if (outOfSync.Count == 0)
            return;

        // File names stand in for display names here; the caller swaps in the real ones once it knows there is
        // something to copy and the resolver is worth waking.
        plans.Add(new SettingsSyncPlan(
            profile, settings.InstallRoot, source, source.FileName,
            outOfSync, outOfSync.Select(file => file.FileName).ToList()));
    }

    private bool _ClientsRunning() =>
        _presence is not null && (_presence.RunningClientCount() > 0 || _presence.Current.CharacterNames.Count > 0);

    private async Task<AutoSyncPass> _FailAsync(string reason, CancellationToken cancellationToken)
    {
        // Only the first of a repeating failure is recorded: a profile folder that stays gone would otherwise write
        // a history entry every fifteen seconds and push every real run out of view.
        if (_lastFailure != reason)
        {
            _lastFailure = reason;
            _logger?.LogWarning("Automatic EVE settings sync did not run: {Reason}", reason);
            await _preferences.AppendAutoSyncRunAsync(
                new AutoSyncRun(_now(), $"Did not run: {reason}.", string.Empty, true), cancellationToken);
        }

        return _Pass(AutoSyncPass.Failed);
    }

    private AutoSyncPass _Pass(AutoSyncPass pass)
    {
        LastPass = pass;
        return pass;
    }
}
