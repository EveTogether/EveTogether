using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Home;

/// <summary>
/// FLEETS (ET-324): every forming or active fleet one of the own characters is in — local or on a coupled server — with
/// its roles filled against their minimum, whether everyone can fly their assigned fit, the members with their hulls,
/// its runs and START; then whatever invites or join requests wait.
///
/// <para><b>Read off the UI thread, and only on a fleet change.</b> A roster change or a server's fleet.changed reloads
/// this block alone; a burst folds into one read. Fleet metrics never reach it — a refresh per metric is what hung the
/// run window in ET-298. START and NEW FLEET go through the fleets screen's own flow, confirmations included.</para>
/// </summary>
public sealed partial class HomeFleetsViewModel : ObservableObject, IDisposable
{
    /// <summary>Lets a burst of fleet.changed and roster news settle into one read.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly IServiceProvider? _services;
    private readonly HomeNavigation _navigation;
    private readonly CharacterFaceCache _faces;
    private readonly IDisposable? _rosterSubscription;
    private readonly ILogger<HomeFleetsViewModel>? _logger;

    private bool _isReading;
    private bool _isReadOwed;
    private bool _isSettling;

    public HomeFleetsViewModel(IServiceProvider? services, HomeNavigation navigation, CharacterFaceCache faces)
    {
        _services = services;
        _navigation = navigation;
        _faces = faces;
        _logger = services?.GetService<ILogger<HomeFleetsViewModel>>();
        // The one watch every fleet screen listens to: a server's fleet.changed and a local roster change alike.
        _rosterSubscription = services?.GetService<IFleetRosterWatch>()?.Subscribe(_ => _Changed());
    }

    public ObservableCollection<HomeFleetCardViewModel> Cards { get; } = [];

    [ObservableProperty] private bool _hasCards;
    [ObservableProperty] private string _countText = string.Empty;
    [ObservableProperty] private string _pendingText = "No pending invites or join requests.";
    [ObservableProperty] private bool _hasPending;

    /// <summary>How many reads this block has done — what the ET-324 measurement counts a fleet storm in.</summary>
    internal int ReadCount { get; private set; }

    [RelayCommand]
    private void OpenFleets() => _ = _navigation.OpenFleets(null);

    [RelayCommand]
    private void NewFleet() => _ = _navigation.OpenFleets(fleets => fleets.NewFleetCommand.ExecuteAsync(null));

    /// <summary>One read at a time; a change landing meanwhile is owed and read straight after (ET-287).</summary>
    public async Task ReadAsync()
    {
        if (_services is not { } services)
            return;

        if (_isReading)
        {
            _isReadOwed = true;
            return;
        }

        _isReading = true;
        try
        {
            do
            {
                _isReadOwed = false;
                ReadCount++;
                try
                {
                    _Show(await Task.Run(() => HomeFleetsReader.ReadAsync(services)));
                }
                catch (Exception ex)
                {
                    // Fire-and-forget from a fleet change: unlogged, a failed read would vanish into an unobserved
                    // task. The cards keep what they showed.
                    _logger?.LogWarning(ex, "The home's fleets could not be read.");
                }
            }
            while (_isReadOwed);
        }
        finally
        {
            _isReading = false;
        }
    }

    /// <summary>Delivered on the UI thread. A change during a read is owed to it; otherwise the burst settles first.</summary>
    private void _Changed() => _ = _ChangedAsync();

    private async Task _ChangedAsync()
    {
        if (_isReading || _isSettling)
        {
            _isReadOwed |= _isReading;
            return;
        }

        _isSettling = true;
        await Task.Delay(Settle);
        _isSettling = false;
        await ReadAsync();
    }

    private void _Show(HomeFleetsRead read)
    {
        Dictionary<(string?, long), HomeFleetCardViewModel> shown = Cards.ToDictionary(card => (card.ServerAddress, card.FleetId));
        List<HomeFleetCardViewModel> cards = [];
        foreach (HomeFleetFacts facts in read.Fleets)
        {
            HomeFleetCardViewModel card = shown.GetValueOrDefault((facts.ServerAddress, facts.Info.Id))
                                          ?? new HomeFleetCardViewModel(facts.ServerAddress, facts.Info.Id, _Start);
            card.Show(facts, _faces, _services?.GetService<ITypeImageProvider>());
            cards.Add(card);
        }

        // A server that could not be read this time keeps what it last showed: its fleets did not go anywhere.
        cards.AddRange(Cards.Where(card => card.ServerAddress is { } address && read.UnreachableServers.Contains(address)
                                           && !cards.Contains(card)));
        Cards.ReconcileTo(cards);
        HasCards = cards.Count > 0;
        int forming = cards.Count(card => card.IsForming);
        int active = cards.Count - forming;
        CountText = string.Join(" · ", new[] { forming > 0 ? $"{forming} forming" : null, active > 0 ? $"{active} active" : null }
            .OfType<string>());
        HasPending = read.PendingInvites + read.PendingJoinRequests > 0;
        PendingText = HasPending
            ? string.Join(" · ", new[]
            {
                read.PendingInvites > 0 ? $"{read.PendingInvites} invite{(read.PendingInvites == 1 ? "" : "s")} waiting" : null,
                read.PendingJoinRequests > 0 ? $"{read.PendingJoinRequests} join request{(read.PendingJoinRequests == 1 ? "" : "s")}" : null
            }.OfType<string>())
            : "No pending invites or join requests.";
    }

    private Task _Start(HomeFleetCardViewModel card) =>
        _navigation.OpenFleets(fleets => fleets.StartFleetAsync(card.FleetId, card.ServerAddress));

    public void Dispose()
    {
        _rosterSubscription?.Dispose();
    }
}

/// <summary>One fleet card: state, where it lives, roles against their minimum, who can fly their fit, the members with
/// their hulls, its runs, and START for the owner of a forming fleet.</summary>
public sealed partial class HomeFleetCardViewModel(string? serverAddress, long fleetId, Func<HomeFleetCardViewModel, Task> start)
    : ObservableObject
{
    public string? ServerAddress { get; } = serverAddress;

    public long FleetId { get; } = fleetId;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isForming;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private string _metaText = string.Empty;
    [ObservableProperty] private string _flyText = string.Empty;
    [ObservableProperty] private string _flyTooltip = string.Empty;
    [ObservableProperty] private bool _hasComposition;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private IReadOnlyList<CompositionRoleCount> _roles = [];

    public ObservableCollection<HomeFleetMemberViewModel> Members { get; } = [];

    internal void Show(HomeFleetFacts facts, CharacterFaceCache faces, ITypeImageProvider? images)
    {
        Name = facts.Info.Name;
        IsForming = facts.Info.Activation == FleetActivation.Forming;
        StateText = IsForming ? "FORMING" : "ACTIVE";
        SourceText = facts.ServerName?.ToUpperInvariant() ?? "LOCAL";
        CanStart = IsForming && facts.IsMine;
        HasComposition = facts.CompositionName is not null;
        if (!Roles.SequenceEqual(facts.Roles))
            Roles = facts.Roles;
        (FlyText, FlyTooltip) = _Fly(facts.Members);
        MetaText = string.Join(" · ", new[]
        {
            facts.CompositionName,
            FlyText,
            facts.Runs switch
            {
                0 => "no runs yet",
                1 => "1 run",
                var runs => $"{runs} runs"
            },
            facts.OwnIsk is { } isk ? IskFormat.Compact(isk) : null,
            facts.LastRunLocal is { } last ? "last " + last.ToString("ddd d MMM", CultureInfo.InvariantCulture) : null
        }.OfType<string>());

        Dictionary<int, HomeFleetMemberViewModel> shown = Members.ToDictionary(member => member.CharacterId);
        List<HomeFleetMemberViewModel> members = [];
        foreach (HomeFleetMemberFacts member in facts.Members)
        {
            HomeFleetMemberViewModel row = shown.GetValueOrDefault(member.CharacterId)
                                           ?? new HomeFleetMemberViewModel(member.CharacterId, faces.FaceOf(member.CharacterId, member.Name));
            row.Show(member, images);
            members.Add(row);
        }

        Members.ReconcileTo(members);
    }

    [RelayCommand]
    private Task Start() => start(this);

    /// <summary>"all 5 can fly their fits"; a member whose skills are not shared is counted as such, never as unable.</summary>
    private static (string Text, string Tooltip) _Fly(IReadOnlyList<HomeFleetMemberFacts> members)
    {
        HomeFleetMemberFacts[] assigned = [.. members.Where(member => member.HullTypeId is not null)];
        if (assigned.Length == 0)
            return ("no fits assigned", "Nobody in this fleet has a fit assigned yet.");

        int canFly = assigned.Count(member => member.Verdict == FitSkillVerdict.CanFly);
        int missing = assigned.Count(member => member.Verdict == FitSkillVerdict.MissingSkills);
        int notShared = assigned.Count(member => member.Verdict == FitSkillVerdict.Unknown && !member.SharesSkills);
        int unknown = assigned.Length - canFly - missing - notShared;
        string tooltip = "Each pilot's own client checks their skills against the assigned fit and reports the verdict.";
        if (canFly == assigned.Length)
            return (assigned.Length == 1 ? "can fly the fit" : $"all {assigned.Length} can fly their fits", tooltip);

        string text = string.Join(" · ", new[]
        {
            $"{canFly} of {assigned.Length} can fly",
            missing > 0 ? $"{missing} missing skills" : null,
            notShared > 0 ? $"{notShared} skills not shared" : null,
            unknown > 0 ? $"{unknown} not reported" : null
        }.OfType<string>());
        return (text, tooltip);
    }
}

/// <summary>A member on a fleet card: the face, and the hull of the fit assigned to them.</summary>
public sealed partial class HomeFleetMemberViewModel(int characterId, CharacterFaceViewModel face) : ObservableObject
{
    public int CharacterId { get; } = characterId;

    public CharacterFaceViewModel Face { get; } = face;

    [ObservableProperty] private Bitmap? _hullIcon;
    [ObservableProperty] private string _tooltip = string.Empty;

    private int? _hullShown;

    internal void Show(HomeFleetMemberFacts member, ITypeImageProvider? images)
    {
        Tooltip = member.HullName is { } hull ? $"{member.Name} · {hull} · {member.FitName}" : $"{member.Name} · no fit assigned";
        if (member.HullTypeId == _hullShown)
            return;

        _hullShown = member.HullTypeId;
        HullIcon = null;
        if (images is not null && member.HullTypeId is { } typeId)
            _ = _LoadHullAsync(images, typeId);
    }

    private async Task _LoadHullAsync(ITypeImageProvider images, int typeId) =>
        HullIcon = await images.GetImageAsync(typeId, TypeImageKind.Icon, 32);
}

internal sealed record HomeFleetMemberFacts(
    int CharacterId, string Name, int? HullTypeId, string? HullName, string? FitName, FitSkillVerdict Verdict, bool SharesSkills);

internal sealed record HomeFleetFacts(
    FleetInfo Info,
    string? ServerAddress,
    string? ServerName,
    bool IsMine,
    string? CompositionName,
    IReadOnlyList<CompositionRoleCount> Roles,
    IReadOnlyList<HomeFleetMemberFacts> Members,
    int Runs,
    decimal? OwnIsk,
    DateTime? LastRunLocal);

internal sealed record HomeFleetsRead(
    IReadOnlyList<HomeFleetFacts> Fleets,
    IReadOnlySet<string> UnreachableServers,
    int PendingInvites,
    int PendingJoinRequests);

/// <summary>Everything the fleets block shows, read in one go off the UI thread through the same clients the fleets
/// screen uses: the server's own list per coupled character, and the client-only fleets from the local store.</summary>
internal static class HomeFleetsReader
{
    public static async Task<HomeFleetsRead> ReadAsync(IServiceProvider services)
    {
        IReadOnlyList<Character> characters = await services.GetRequiredService<ICharacterRegistry>().GetAllAsync();
        Dictionary<int, Character> own = characters
            .Where(character => character.EsiCharacterId is > 0)
            .GroupBy(character => character.EsiCharacterId ?? 0)
            .ToDictionary(group => group.Key, group => group.First());
        var names = new RunsCharacterNames(own.ToDictionary(pair => (long)pair.Key, pair => pair.Value.Name),
            services.GetService<IExternalCharacterLookup>());
        IFleetTransportClient transport = services.GetRequiredService<IFleetTransportClient>();
        ClientFleetService localFleets = services.GetRequiredService<ClientFleetService>();
        IFleetReader repository = services.GetRequiredService<IFleetReader>();
        ICharacterRegistry registry = services.GetRequiredService<ICharacterRegistry>();
        IFleetCompositionReader compositions = services.GetRequiredService<IFleetCompositionReader>();

        List<HomeFleetFacts> fleets = [];
        HashSet<string> unreachable = [];
        int invites = 0;
        int joinRequests = 0;

        foreach (int ownerId in own.Keys)
            foreach (var entity in await repository.ListByCreatorAsync(ownerId))
            {
                if (!entity.IsClientOnly || entity.State != FleetState.Active || entity.Activation == FleetActivation.Concluded)
                    continue;

                fleets.Add(await _FactsAsync(services, FleetInfo.FromEntity(entity), null, null, ownerId, true,
                    new LocalFleetClient(localFleets, repository, registry, ownerId),
                    new LocalFleetCompositionClient(localFleets, compositions, ownerId), own, names));
            }

        IClientSessionStore sessions = services.GetRequiredService<IClientSessionStore>();
        IServerRegistry servers = services.GetRequiredService<IServerRegistry>();
        foreach (string server in await sessions.ListServersAsync())
        {
            string serverName = await servers.DisplayNameAsync(server);
            HashSet<long> seen = [];
            try
            {
                foreach (ClientSessionTokens session in await sessions.LoadAllAsync(server))
                {
                    invites += (await transport.ListPendingInvitesAsync(server, session.CharacterId)).Count;
                    foreach (FleetInfo fleet in await transport.ListMyFleetsAsync(server, session.CharacterId))
                    {
                        if (fleet.State != FleetState.Active || fleet.Activation == FleetActivation.Concluded || !seen.Add(fleet.Id))
                            continue;

                        bool isMine = fleet.CreatorCharacterId == session.CharacterId;
                        var client = new ServerFleetClient(transport, server, session.CharacterId);
                        if (isMine)
                            joinRequests += (await client.ListPendingJoinRequestsAsync(fleet.Id)).Count;
                        fleets.Add(await _FactsAsync(services, fleet, server, serverName, session.CharacterId, isMine, client,
                            new ServerFleetCompositionClient(transport, server, session.CharacterId), own, names));
                    }
                }
            }
            catch (FleetTransportException)
            {
                unreachable.Add(server);
            }
        }

        // The one sweep a pilot gets without opening anything: refreshing membership with the home is what gives a run
        // window its fleet on a client whose fleets window was never opened (ET-152).
        if (services.GetService<FleetParticipationRefresher>() is { } participation)
            await participation.RefreshAsync();

        return new HomeFleetsRead(
            [.. fleets.OrderBy(fleet => fleet.Info.Activation == FleetActivation.Forming ? 0 : 1).ThenBy(fleet => fleet.Info.Name)],
            unreachable, invites, joinRequests);
    }

    private static async Task<HomeFleetFacts> _FactsAsync(IServiceProvider services, FleetInfo fleet, string? server,
        string? serverName, int actingCharacterId, bool isMine, IFleetClient client, IFleetCompositionClient compositions,
        IReadOnlyDictionary<int, Character> own, RunsCharacterNames names)
    {
        IReadOnlyList<FleetMemberInfo> members = await client.ListMembersAsync(fleet.Id);
        FleetCompositionDetail? composition = fleet.FleetCompositionId is { } compositionId
            ? await compositions.GetAsync(compositionId)
            : null;
        await names.HydrateAsync(members.Select(member => (long)member.CharacterId));
        var fitNames = ViewModels.FitBrowser.FitNameResolverFactory.For(services);

        // The fleet's runs as the runs overview filters them (FleetId), with this machine's own share.
        Result<IReadOnlyList<ActivityOverviewRowDto>> runs = await services.GetRequiredService<CqrsDispatcher>()
            .Query(new GetActivityOverviewQuery(FleetId: fleet.Id, OwnCharacterIds: [.. own.Keys.Select(id => (long)id)]));
        IReadOnlyList<ActivityOverviewRowDto> rows = runs.IsSuccess ? runs.Value ?? [] : [];
        decimal[] ownIsk = [.. rows.Where(row => row.OwnIsk.HasFigure).Select(row => row.OwnIsk.Total)];

        return new HomeFleetFacts(
            fleet, server, serverName, isMine, composition?.Composition.Name,
            CompositionFillBuilder.RoleCounts(composition, members),
            [.. members.Select(member => new HomeFleetMemberFacts(
                member.CharacterId,
                names.NameOf(member.CharacterId),
                member.AssignedFit?.ShipTypeId,
                member.AssignedFit is { } fit ? fitNames.TypeName(fit.ShipTypeId) : null,
                member.AssignedFit?.FitName,
                member.FitSkillVerdict,
                !own.TryGetValue(member.CharacterId, out Character? character) || character.HasScope(SkillsScopeCatalog.ReadSkills)))],
            rows.Count,
            ownIsk.Length == 0 ? null : ownIsk.Sum(),
            rows.Count == 0 ? null : rows.Max(row => row.StartedAtUtc).ToLocalTime());
    }
}
