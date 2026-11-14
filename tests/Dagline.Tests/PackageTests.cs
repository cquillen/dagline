using System.Reflection;

namespace Dagline.Tests;

public class PackageTests
{
    [Test]
    public void The_package_references_nothing_but_the_runtime()
    {
        var references = typeof(Runner).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name != "System" && !name.StartsWith("System.", StringComparison.Ordinal) && name != "netstandard");

        references.ShouldBeEmpty();
    }

    // A concrete type must override every abstract member of its base, and code outside Dagline cannot override one it cannot see,
    // so an abstract member hidden from it is what keeps it from deriving a concrete type, whatever constructors the base has.
    [Test]
    [Arguments(typeof(RunEvent))]
    [Arguments(typeof(Journal))]
    [Arguments(typeof(Node))]
    public void A_closed_abstract_type_has_an_abstract_member_that_code_outside_Dagline_cannot_override(Type closed)
    {
        var hidden = closed
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.IsAbstract && (method.IsAssembly || method.IsFamilyAndAssembly));

        hidden.ShouldNotBeEmpty();
    }
}
