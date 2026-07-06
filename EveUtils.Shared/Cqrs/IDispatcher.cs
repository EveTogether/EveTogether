namespace EveUtils.Shared.Cqrs;

/// <summary>
/// Central in-process dispatcher of queries and commands to their handler. The UI/host
/// only knows the query/command types, not the handlers — that keeps the modules decoupled.
/// </summary>
public interface IDispatcher
{
    Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);

    Task Send(ICommand command, CancellationToken cancellationToken = default);

    Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
}
