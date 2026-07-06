namespace EveUtils.Shared.DependencyInjection;

/// <summary>Marker: a service auto-registered with a transient lifetime against its implemented interfaces
/// by the central auto-registration scan (see <see cref="ModuleRegistrationExtensions.AddAutoServices"/>).</summary>
public interface ITransientService;
