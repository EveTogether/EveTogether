using System;

namespace EveUtils.Client.Fleet;

/// <summary>
/// An <see cref="IFleetClient"/> that can hand out the doctrine client bound to its own context (ET-171).
///
/// The two go together everywhere they are built — a server fleet's roster gets a
/// <see cref="ServerFleetCompositionClient"/> on the same address and acting character, a client-only fleet's gets a
/// <see cref="LocalFleetCompositionClient"/> for the same owner — but only the fleet client itself is carried around
/// afterwards. That was fine while the roster was only ever opened from the overview, which holds both halves. Fleet
/// metrics opens it too now, and metrics is handed the fleet client alone.
///
/// Without this, the roster reached from metrics would be handed <c>compositions: null</c> and quietly lose its
/// doctrine section — the same screen showing less because of the door you came through. The context needed to build
/// the doctrine client is exactly what each fleet client already bound at construction, so it is the fleet client
/// that answers.
/// </summary>
public interface IFleetCompositionClientSource
{
    /// <summary>
    /// The doctrine client for this fleet client's own context. <paramref name="services"/> supplies what the
    /// implementation does not already hold itself (the client-only variant reads the composition repository from
    /// there); it is not a way to reach a different fleet's context.
    /// </summary>
    IFleetCompositionClient CreateCompositionClient(IServiceProvider services);
}
