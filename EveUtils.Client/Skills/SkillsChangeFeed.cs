using EveUtils.Client.Messaging;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Events;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Skills;

/// <summary>
/// The one place the SKILLS window hears that a character's skills, queue or attributes changed (ET-387), whether
/// from its own import or a background refresh. The window, the UI thread and the one-reload-at-a-time rule are
/// <see cref="ChangeFeed{TEvent}"/>'s.
/// </summary>
public sealed class SkillsChangeFeed(IEventBus eventBus, ILogger<SkillsChangeFeed> logger, TimeSpan window)
    : ISingletonService, IDisposable
{
    private readonly ChangeFeed<SkillsChangedEvent> _feed = new(eventBus, logger, window);

    public SkillsChangeFeed(IEventBus eventBus, ILogger<SkillsChangeFeed> logger)
        : this(eventBus, logger, ChangeFeed<SkillsChangedEvent>.DefaultWindow)
    {
    }

    public IDisposable Subscribe(Func<IReadOnlyList<SkillsChangedEvent>, Task> reload) => _feed.Subscribe(reload);

    public void Dispose() => _feed.Dispose();
}
