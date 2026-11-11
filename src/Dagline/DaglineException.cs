namespace Dagline;

/// <summary>
/// The base of every exception that Dagline defines. Catch it to handle any of them in one place.
/// </summary>
/// <remarks>
/// Dagline throws one of these when it refuses what it was given, before any task runs.
/// A mistake in a single call, such as a <see langword="null"/> argument, throws the usual .NET exception instead,
/// and a failure inside a task never throws from the run: it fails that task.
/// </remarks>
public class DaglineException : Exception
{
    /// <summary>Creates the exception with the message that describes it.</summary>
    /// <param name="message">What Dagline refused, and why.</param>
    protected DaglineException(string message)
        : base(message)
    {
    }
}
