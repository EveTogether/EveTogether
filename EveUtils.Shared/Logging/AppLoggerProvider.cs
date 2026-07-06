using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Logging;

/// <summary>
/// Registers the <see cref="AppLogger"/> instances into the .NET logging pipeline.
/// Wire via <c>builder.Logging.AddProvider(new AppLoggerProvider(store))</c> or the
/// <see cref="LoggingBuilderExtensions.AddAppLogStore"/> extension.
/// </summary>
[ProviderAlias("AppLogStore")]
public sealed class AppLoggerProvider(ILogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new AppLogger(categoryName, store);

    public void Dispose() { }
}
