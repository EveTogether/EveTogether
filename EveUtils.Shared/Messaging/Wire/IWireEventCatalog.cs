namespace EveUtils.Shared.Messaging.Wire;

/// <summary>A module's wire-event registrations (which events travel over the remote bus, and how).</summary>
public interface IWireEventCatalog
{
    void RegisterInto(IEventTypeRegistry registry);
}
