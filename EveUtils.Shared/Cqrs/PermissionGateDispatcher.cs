using System.Collections.Concurrent;
using System.Reflection;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Cqrs;

/// <summary>
/// Decorates the <see cref="Dispatcher"/> with the permission gate (foundation pillar 2): a
/// pipeline step that reads <see cref="RequiresPermissionAttribute"/> from the concrete message type
/// and consults <see cref="IAccessPolicy"/> before the inner dispatcher runs. Call sites are
/// unchanged. Commands returning a <see cref="Result"/> envelope fail gracefully (PERMISSION_DENIED);
/// everything else throws <see cref="PermissionDeniedException"/>.
/// </summary>
public sealed class PermissionGateDispatcher(IDispatcher inner, IAccessPolicy policy, IPrincipalAccessor principals)
    : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, string?> CodeCache = new();

    public async Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        await EnsureAllowedAsync(query.GetType(), cancellationToken);
        return await inner.Query(query, cancellationToken);
    }

    public async Task Send(ICommand command, CancellationToken cancellationToken = default)
    {
        await EnsureAllowedAsync(command.GetType(), cancellationToken);
        await inner.Send(command, cancellationToken);
    }

    public async Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        var code = GetCode(command.GetType());
        if (code is not null && !await policy.IsAllowedAsync(principals.Current, code, cancellationToken))
        {
            // A command that returns an envelope reports the denial in-band; otherwise we throw.
            if (TryCreateFailure<TResult>(code, out var failure))
                return failure;
            throw new PermissionDeniedException(code);
        }

        return await inner.Send(command, cancellationToken);
    }

    private async Task EnsureAllowedAsync(Type messageType, CancellationToken cancellationToken)
    {
        var code = GetCode(messageType);
        if (code is not null && !await policy.IsAllowedAsync(principals.Current, code, cancellationToken))
            throw new PermissionDeniedException(code);
    }

    private static string? GetCode(Type messageType) =>
        CodeCache.GetOrAdd(messageType, static t => t.GetCustomAttribute<RequiresPermissionAttribute>()?.Code);

    private static bool TryCreateFailure<TResult>(string code, out TResult failure)
    {
        var message = new ResultMessage(
            MessageSeverity.Error, MessageCodes.PermissionDenied, $"Permission '{code}' is required.");

        var resultType = typeof(TResult);
        if (resultType == typeof(Result))
        {
            failure = (TResult)(object)Result.Failure(message);
            return true;
        }

        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            // Result<T> hides Result.Failure with its own static method — bind to the declared one.
            var failureMethod = resultType.GetMethod(
                nameof(Result.Failure), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            failure = (TResult)failureMethod!.Invoke(null, [new[] { message }])!;
            return true;
        }

        failure = default!;
        return false;
    }
}
