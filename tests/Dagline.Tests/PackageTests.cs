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
}
