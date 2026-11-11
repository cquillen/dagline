namespace Dagline;

/// <summary>
/// The base of every exception that Dagline itself throws. Catch it to handle any of them in one place.
/// </summary>
/// <remarks>
/// An argument that is wrong at a call, such as <see langword="null"/>, throws the usual <see cref="ArgumentException"/>
/// and not this type. An exception that escapes a task body is never thrown at the app either.
/// </remarks>
public class DaglineException : Exception
{
    /// <summary>Creates an exception with a default message.</summary>
    public DaglineException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public DaglineException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the exception that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DaglineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
