using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Cqrs;

/// <summary>
/// Resolves the correct handler from the DI container based on the concrete query/
/// command type and calls <c>Handle</c>. Stateless — the scope (and therefore the DbContext)
/// comes from the calling scope.
/// </summary>
public sealed class Dispatcher(IServiceProvider provider) : IDispatcher
{
    public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
        var handler = provider.GetRequiredService(handlerType);
        return (Task<TResult>)Invoke(handlerType, handler, query, cancellationToken);
    }

    public Task Send(ICommand command, CancellationToken cancellationToken = default)
    {
        var handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
        var handler = provider.GetRequiredService(handlerType);
        return (Task)Invoke(handlerType, handler, command, cancellationToken);
    }

    public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
        var handler = provider.GetRequiredService(handlerType);
        return (Task<TResult>)Invoke(handlerType, handler, command, cancellationToken);
    }

    private static object Invoke(Type handlerType, object handler, object message, CancellationToken cancellationToken)
    {
        var method = handlerType.GetMethod("Handle")
            ?? throw new InvalidOperationException($"Handler {handlerType} is missing a Handle method.");
        return method.Invoke(handler, [message, cancellationToken])!;
    }
}
