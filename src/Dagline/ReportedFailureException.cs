namespace Dagline;

/// <summary>
/// The exception a task body throws to end as a reported failure: <c>throw context.Fail("2 bad entities");</c>.
/// Only <see cref="TaskContext.Fail"/> creates one.
/// </summary>
/// <remarks>
/// <para>
/// The exception is a way out of the body and nothing more. It is the call to <see cref="TaskContext.Fail"/> that records
/// the reported failure, so the task ends failed as a reported failure whether the body throws this exception, throws another,
/// or returns.
/// </para>
/// <para>
/// One that escapes the body of a task whose own context did not record a failure, such as one caught from another task
/// and thrown again, is an unexpected failure of that task like any other exception.
/// </para>
/// </remarks>
public sealed class ReportedFailureException : Exception
{
    internal ReportedFailureException(string message, IReadOnlyList<string> detail)
        : base(message)
    {
        Detail = detail;
    }

    /// <summary>
    /// The detail lines given to the call to <see cref="TaskContext.Fail"/> that made this exception, with text that holds
    /// line breaks split into one line for each. Empty when the call gave none.
    /// </summary>
    /// <remarks>
    /// These are the lines of that one call, all of them. <see cref="NodeSnapshot.Detail"/> has every detail line of the task
    /// up to the volume limit.
    /// </remarks>
    public IReadOnlyList<string> Detail { get; }
}
