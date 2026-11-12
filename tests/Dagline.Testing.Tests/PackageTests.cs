using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Dagline.TestSupport;

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

    [Test]
    [NotInParallel(DiskHeavy.Key)]
    public async Task The_build_packs_it_with_the_other_two_packages()
    {
        var root = RepositoryRoot();
        var projects = XDocument.Load(Path.Combine(root, "Dagline.slnx"))
            .Descendants("Project")
            .Select(project => project.Attribute("Path")!.Value);

        var evaluated = await Task.WhenAll(projects.Select(project => PackageIdIfPackable(root, project)));

        evaluated.OfType<string>().Order(StringComparer.Ordinal).ShouldBe(["Dagline", "Dagline.Spectre", "Dagline.Testing"]);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Dagline.slnx")))
        {
            directory = directory.Parent ?? throw new InvalidOperationException("No Dagline.slnx above the test assembly.");
        }

        return directory.FullName;
    }

    // Evaluating the project, not building or packing it, keeps this to well under a second a project.
    private static async Task<string?> PackageIdIfPackable(string root, string project)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            ArgumentList = { "msbuild", Path.Combine(root, project), "-getProperty:IsPackable", "-getProperty:PackageId" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, output);

        var properties = JsonElement.Parse(output).GetProperty("Properties");
        return properties.GetProperty("IsPackable").GetString() == "true" ? properties.GetProperty("PackageId").GetString() : null;
    }
}
