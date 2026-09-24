using System.Reflection;
using System.Runtime.CompilerServices;
using EveUtils.Shared.Cqrs;

namespace EveUtils.Server.Tests;

/// <summary>
/// Finds the types that take a write repository in their constructor without being a command handler (ET-383). Shared
/// by the server and the client test projects (the client project links this file), each holding the assemblies it
/// can load and its own list of reasons.
///
/// <para>A write repository is an <c>I*Repository</c> of a module whose commands publish a change signal. Those are the
/// writes the signal rule is about: one that bypasses the handlers bypasses the signal, and nobody hears it. Each such
/// repository has a reader half (<c>I*Reader</c>) for everything else. The guard reads constructors only: a type that
/// resolves a store per call through the service provider (the <c>Checks</c> self-tests, the activity trackers'
/// <c>TouchActivity</c>) is outside what it sees.</para>
/// </summary>
internal static class WriteRepositoryGuard
{
    private const string ModulesNamespace = "EveUtils.Shared.Modules.";
    private const string RepositoriesSuffix = ".Repositories";

    /// <summary>The modules whose commands publish a change signal, as <c>CommandSignalCoverageTests</c> maps them.</summary>
    private static readonly string[] SignallingModules =
        ["Runs", "Fleet", "Fleet.Composition", "Fittings", "Killmails", "Messaging", "Ships"];

    public static bool IsWriteRepository(Type type) =>
        type.IsInterface
        && type.Assembly == typeof(ICommandHandler<>).Assembly
        && type.Name.EndsWith("Repository", StringComparison.Ordinal)
        && type.Namespace is { } ns
        && ns.StartsWith(ModulesNamespace, StringComparison.Ordinal)
        && ns.EndsWith(RepositoriesSuffix, StringComparison.Ordinal)
        && SignallingModules.Contains(ns[ModulesNamespace.Length..^RepositoriesSuffix.Length]);

    /// <summary>Every type in <paramref name="assembly"/> other than a command handler whose constructor takes a write
    /// repository, with the repositories it takes.</summary>
    public static IReadOnlyDictionary<Type, Type[]> TakersOutsideCommandHandlers(Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                           && !_IsCommandHandler(type))
            .Select(type => (Type: type, Repositories: type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .Where(IsWriteRepository)
                .Distinct()
                .ToArray()))
            .Where(taker => taker.Repositories.Length > 0)
            .ToDictionary(taker => taker.Type, taker => taker.Repositories);

    private static bool _IsCommandHandler(Type type) =>
        type.GetInterfaces().Any(contract => contract.IsGenericType
                                             && (contract.GetGenericTypeDefinition() == typeof(ICommandHandler<>)
                                                 || contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)));
}
