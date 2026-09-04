namespace EveUtils.Shared.DependencyInjection;

/// <summary>
/// Marks a type that only the <b>client</b> host can compose, so the server's scan skips it instead of
/// registering something it cannot construct.
///
/// This is not the same axis as <c>IRuntimeContext.Host</c>. That one answers "behave differently depending on
/// where I run" at runtime, on a type both hosts can build. This one answers "I cannot be built here at all",
/// which has to be decided while the container is being composed — a type whose constructor asks for
/// <c>ClientDbContext</c> can never be resolved on the server, and registering it there turns a design fact
/// into a startup crash (ET-180).
///
/// Reach for it only when the dependency itself is host-bound. A type that merely reads the client database
/// through <c>IDbContextFactory&lt;SharedDbContext&gt;</c> is constructible on both hosts and needs no marker.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ClientOnlyAttribute : Attribute;
