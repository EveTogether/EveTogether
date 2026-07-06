using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Logging;

/// <summary>DI helpers for the in-app log store.</summary>
public static class LoggingBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryLogStore"/> as <see cref="ILogStore"/> (singleton) and adds
    /// <see cref="AppLoggerProvider"/> to the logging pipeline so Warning and above entries are captured.
    /// </summary>
    public static IServiceCollection AddAppLogStore(this IServiceCollection services, string? dataDirectory = null)
    {
        var store = new InMemoryLogStore(dataDirectory: dataDirectory);
        services.AddSingleton<ILogStore>(store);
        services.AddLogging(b => b.AddProvider(new AppLoggerProvider(store)));
        return services;
    }
}
