using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Dagline;

/// <summary>The checks of an argument that the framework's own <c>ThrowIf</c> helpers do not cover.</summary>
internal static class Arguments
{
    /// <summary>
    /// Gives back an argument that is not <see langword="null"/>. It is <see cref="ArgumentNullException.ThrowIfNull(object?, string?)"/>
    /// for where the check has to be an expression: a property of a positional record, which has no constructor body to check in.
    /// </summary>
    /// <param name="argument">The argument to check.</param>
    /// <param name="paramName">The name of the parameter, which the compiler fills in from the expression given.</param>
    /// <exception cref="ArgumentNullException"><paramref name="argument"/> is <see langword="null"/>.</exception>
    public static T NotNull<T>([NotNull] T? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(argument, paramName);
        return argument;
    }

    /// <summary>Throws when a collection that a consumer passed holds a <see langword="null"/> element.</summary>
    /// <param name="elements">The collection, which is already known not to be <see langword="null"/> itself.</param>
    /// <param name="message">What is wrong, as in <c>A line is null.</c></param>
    /// <param name="paramName">The name of the parameter the collection came in.</param>
    /// <exception cref="ArgumentException">An element of <paramref name="elements"/> is <see langword="null"/>.</exception>
    public static void ThrowIfAnyNull<T>(IEnumerable<T?> elements, string message, string paramName)
        where T : class
    {
        if (elements.Contains(null))
        {
            throw new ArgumentException(message, paramName);
        }
    }
}
