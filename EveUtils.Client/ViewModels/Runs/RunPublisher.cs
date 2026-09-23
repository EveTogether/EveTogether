using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Messaging;
using EveUtils.Client.Notifications;
using EveUtils.Client.Runs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>How a publish ended, for the caller to show where its reader is looking.</summary>
/// <param name="RunsChanged">Runs were queued (and maybe accepted): what shows them is out of date.</param>
public sealed record RunPublishOutcome(string Message, ToastKind Kind, string Title, bool RunsChanged);

/// <summary>
/// Publishing saved activities to a coupled server (ET-161, RO-6): pick the target as the fit browser does (one coupled
/// server goes without asking), say what travels, then queue and synchronise. The runs overview and the home (ET-324)
/// both publish through here, so the confirmation a pilot reads is the same wherever they press PUBLISH.
///
/// Only runs of characters coupled to that server are queued — the server refuses a run pushed by anyone but its owner,
/// so a crewmate's run would sit Pending for a push that can never be accepted.
/// </summary>
public sealed class RunPublisher(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services)
{
    private const string DefaultTitle = "Publish to server";

    /// <summary>Null when there was nothing to say — no session store to publish through.</summary>
    public async Task<RunPublishOutcome?> PublishOneAsync(Guid activitySummaryId, string siteText)
    {
        if (services.GetService<IClientSessionStore>() is not { } sessionStore)
            return null;

        (string? targetAddress, RunPublishOutcome? refusal) =
            await _PickConnectedServerAsync(sessionStore, $"Publish '{siteText}' to which server?");
        if (targetAddress is null)
            return refusal;

        Result<ActivityDetailDto> detail = await Task.Run(() => dispatcher.Query(new GetActivityDetailQuery(activitySummaryId)));
        if (!detail.IsSuccess || detail.Value is null)
            return _Outcome(detail.Messages.Count > 0 ? detail.Messages[0].Text : "The activity could not be read.", ToastKind.Error);

        IReadOnlyList<ClientSessionTokens> coupled = await Task.Run(() => sessionStore.LoadAllAsync(targetAddress));
        List<ActivityRunDetailDto> ownRuns = [.. detail.Value.Runs
            .Where(run => coupled.Any(session => session.CharacterId == run.CharacterId))];
        if (ownRuns.Count == 0)
            return _Outcome("No run in this activity belongs to a character coupled to that server.", ToastKind.Warning);

        string serverName = await _NameOfAsync(targetAddress);
        if (!await dialogs.ConfirmAsync($"Publish to {serverName}?", _WhatTravels(ownRuns.Count, serverName), "Publish"))
            return _Outcome("Publish cancelled.", ToastKind.Information);

        if (await _QueueAsync(ownRuns.Select(run => run.RunId), targetAddress) is { } refused)
            return _Outcome(refused.Messages.Count > 0 ? refused.Messages[0].Text : "The run could not be queued.", ToastKind.Error);

        (bool accepted, string message) =
            await Task.Run(() => _SynchronizeAsync(targetAddress, [.. ownRuns.Select(run => run.CharacterId)]));
        // The runs stay Pending on a rejection on purpose: they are still meant for this server, so the next publish
        // retries them rather than the pilot having to notice they never arrived.
        return accepted
            ? new RunPublishOutcome($"Published to {serverName}.", ToastKind.Success, "Activity published", true)
            : new RunPublishOutcome($"Publish rejected: {message}", ToastKind.Error, "Publish rejected", true);
    }

    /// <summary>Many activities in one go (RO-6): the same confirmation, queueing and per-character synchronise as one,
    /// batched so a day of 28 is one dialog and one sync per character, never 28.</summary>
    /// <param name="isBusy">Told once the target is settled and the batch is under way, and again when it is over — a
    /// caller disables its PUBLISH buttons meanwhile, since two batches would queue the same run twice.</param>
    public async Task<RunPublishOutcome?> PublishManyAsync(IReadOnlyList<Guid> activitySummaryIds, Action<bool> isBusy)
    {
        if (activitySummaryIds.Count == 0 || services.GetService<IClientSessionStore>() is not { } sessionStore)
            return null;

        (string? targetAddress, RunPublishOutcome? refusal) =
            await _PickConnectedServerAsync(sessionStore, $"Publish {activitySummaryIds.Count} activities to which server?");
        if (targetAddress is null)
            return refusal;

        isBusy(true);
        try
        {
            Result<IReadOnlyList<ActivityRunForPublishDto>> read =
                await Task.Run(() => dispatcher.Query(new GetActivityRunsForPublishQuery(activitySummaryIds)));
            if (!read.IsSuccess || read.Value is null)
                return _Outcome(read.Messages.Count > 0 ? read.Messages[0].Text : "The activities could not be read.", ToastKind.Error);

            IReadOnlyList<ClientSessionTokens> coupled = await Task.Run(() => sessionStore.LoadAllAsync(targetAddress));
            List<ActivityRunForPublishDto> ownRuns = [.. read.Value
                .Where(run => coupled.Any(session => session.CharacterId == run.CharacterId))];
            if (ownRuns.Count == 0)
                return _Outcome("No run in these activities belongs to a character coupled to that server.", ToastKind.Warning);

            int publishedActivities = ownRuns.Select(run => run.ActivitySummaryId).Distinct().Count();
            int skippedActivities = activitySummaryIds.Count - publishedActivities;

            string serverName = await _NameOfAsync(targetAddress);
            if (!await dialogs.ConfirmAsync($"Publish {activitySummaryIds.Count} activities to {serverName}?",
                    _WhatTravels(ownRuns.Count, serverName), "Publish"))
                return _Outcome("Publish cancelled.", ToastKind.Information);

            if (await _QueueAsync(ownRuns.Select(run => run.RunId), targetAddress) is { } refused)
                return _Outcome(refused.Messages.Count > 0 ? refused.Messages[0].Text : "A run could not be queued.", ToastKind.Error);

            (bool accepted, string message) = await Task.Run(
                () => _SynchronizeAsync(targetAddress, [.. ownRuns.Select(run => run.CharacterId).Distinct()]));
            if (!accepted)
                return new RunPublishOutcome($"Publish rejected: {message}", ToastKind.Error, "Publish rejected", true);

            string outcome = skippedActivities > 0
                ? $"Published {publishedActivities} of {activitySummaryIds.Count} to {serverName} · {skippedActivities} "
                  + $"had no run of a character coupled to {serverName}"
                : $"Published {publishedActivities} to {serverName}.";
            return new RunPublishOutcome(outcome, ToastKind.Success, "Activities published", true);
        }
        finally
        {
            isBusy(false);
        }
    }

    private async Task<(string? Address, RunPublishOutcome? Refusal)> _PickConnectedServerAsync(
        IClientSessionStore sessionStore, string prompt)
    {
        IReadOnlyList<string> servers = await Task.Run(() => sessionStore.ListServersAsync());
        if (servers.Count == 0)
            return (null, _Outcome("Not coupled to any server — couple a character first.", ToastKind.Warning));

        string? targetAddress = servers.Count == 1 ? servers[0] : await _SelectServerAsync(servers, prompt);
        if (targetAddress is null)
            return (null, _Outcome("Publish cancelled.", ToastKind.Information));

        return services.GetService<IRemoteBusConnector>()?.StateFor(targetAddress) == ServerConnectionState.Connected
            ? (targetAddress, null)
            : (null, _Outcome("Not connected to that server.", ToastKind.Warning));
    }

    private async Task<Result?> _QueueAsync(IEnumerable<Guid> runIds, string targetAddress)
    {
        Guid[] queued = [.. runIds];
        return await Task.Run(async () =>
        {
            foreach (Guid runId in queued)
            {
                Result result = await dispatcher.Send(new QueueRunForServerSyncCommand(runId, targetAddress));
                if (!result.IsSuccess)
                    return result;
            }

            return (Result?)null;
        });
    }

    /// <summary>One synchronisation per owning character: the server attributes a push to the session it came in on,
    /// so two of this machine's pilots in the same activity — or the same day — are two pushes, not one. Stops at
    /// the first refusal — what the server said about it is worth more than a second attempt's message.</summary>
    private async Task<(bool Accepted, string Message)> _SynchronizeAsync(string targetAddress, IReadOnlyList<long> characterIds)
    {
        using IServiceScope scope = services.CreateScope();
        RunSynchronizationService synchronization = scope.ServiceProvider.GetRequiredService<RunSynchronizationService>();
        foreach (long characterId in characterIds.Distinct())
        {
            (bool accepted, string message) = await synchronization.SynchronizeAsync(targetAddress, characterId);
            if (!accepted)
                return (false, message);
        }

        return (true, string.Empty);
    }

    private async Task<string?> _SelectServerAsync(IReadOnlyList<string> servers, string prompt)
    {
        var options = new List<ServerPickOption>();
        foreach (string address in servers)
            options.Add(new ServerPickOption(address, await _NameOfAsync(address)));
        return await dialogs.SelectServerAsync(prompt, options);
    }

    private async Task<string> _NameOfAsync(string address) =>
        services.GetService<IServerRegistry>() is { } registry ? await registry.DisplayNameAsync(address) : address;

    private static RunPublishOutcome _Outcome(string message, ToastKind kind) => new(message, kind, DefaultTitle, false);

    /// <summary>
    /// What the pilot is about to hand over, named rather than summarised as "this run will be shared". A run is not
    /// a fit: a fit is a list of modules, a run is what you earned, what you flew and where you were. Someone who
    /// presses publish has to know they are telling a server operator their location.
    /// </summary>
    private static string _WhatTravels(int runCount, string serverName) =>
        $"{runCount} of your runs go to {serverName}. Three things travel with them.\n\n"
        + "What you earned — every loot line with its item, quantity and price, and every bounty payout.\n"
        + "The fit you flew — by name.\n"
        + "Where you were — the solar system, and the signature if the run recorded one.\n\n"
        + "The operator of that server can read all of it. Other pilots see it only if they flew this activity with you.";
}
