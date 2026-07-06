namespace EveUtils.Shared.Cqrs;

/// <summary>Marker for a command without a result (write).</summary>
public interface ICommand;

/// <summary>Marker for a command that returns <typeparamref name="TResult"/> (write).</summary>
public interface ICommand<TResult>;
