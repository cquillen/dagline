namespace Dagline.Testing.Tests;

public class PackageTests
{
    [Test]
    public void The_package_references_nothing_but_dagline_and_the_runtime()
    {
        var references = typeof(InstantClock).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name != "System" && !name.StartsWith("System.", StringComparison.Ordinal) && name != "netstandard");

        references.ShouldBe(["Dagline"]);
    }
}
