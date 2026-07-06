namespace EveUtils.Shared.DependencyInjection;

/// <summary>
/// Marker for a repository: its implementation is auto-registered with a scoped lifetime against its
/// implemented interfaces when its assembly is scanned (see <see cref="ModuleRegistrationExtensions.AddAutoServices"/>).
/// Mirrors the DDD-template's <c>IRepository&lt;TEntity&gt;</c> convention, but kept as a plain marker because
/// the EVE-Utils repositories don't share a generic base.
/// </summary>
public interface IRepository;
