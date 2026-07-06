namespace EveUtils.Shared.Messaging;

/// <summary>
/// A single structured message: severity + machine-readable code (see <see cref="MessageCodes"/>)
/// + human-readable text + source (module/host). The same type will later be used over the gRPC
/// envelope and on the local event bus.
/// </summary>
public record ResultMessage(MessageSeverity Severity, string Code, string Text, string? Source = null);
