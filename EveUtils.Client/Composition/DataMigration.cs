namespace EveUtils.Client.Composition;

/// <param name="Root">The folder the client uses this run: the new one, or the legacy one when the move failed.</param>
public sealed record DataMigration(string Root, DataMigrationOutcome Outcome, Exception? Error = null);
