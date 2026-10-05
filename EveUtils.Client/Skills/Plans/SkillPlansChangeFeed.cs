using EveUtils.Client.Messaging;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Events;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Skills.Plans;

/// <summary>
/// The one place the PLANS tab hears that a plan changed (ET-355), whether from its own write or another window's.
/// The window, the UI thread and the one-reload-at-a-time rule are <see cref="ChangeFeed{TEvent}"/>'s.
/// </summary>
public sealed class SkillPlansChangeFeed(IEventBus eventBus, ILogger<SkillPlansChangeFeed> logger, TimeSpan window)
    : ISingletonService, IDisposable
{
    private readonly ChangeFeed<SkillPlansChangedEvent> _feed = new(eventBus, logger, window);

    public SkillPlansChangeFeed(IEventBus eventBus, ILogger<SkillPlansChangeFeed> logger)
        : this(eventBus, logger, ChangeFeed<SkillPlansChangedEvent>.DefaultWindow)
    {
    }

    public IDisposable Subscribe(Func<IReadOnlyList<SkillPlansChangedEvent>, Task> reload) => _feed.Subscribe(reload);

    public void Dispose() => _feed.Dispose();
}
