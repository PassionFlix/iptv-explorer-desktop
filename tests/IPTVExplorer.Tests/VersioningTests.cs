using System.Reflection;
using System.Xml.Linq;
using IPTVExplorer.Core;

namespace IPTVExplorer.Tests;

public sealed class VersioningTests
{
    [Fact]
    public void RuntimeVersionIsDerivedFromDirectoryBuildProps()
    {
        var root = RepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var declared = document.Descendants("Version").Single().Value;

        Assert.Equal(declared, ApplicationVersion.Display);
        Assert.Equal("$(Version).0", document.Descendants("AssemblyVersion").Single().Value);
        Assert.Equal("$(Version).0", document.Descendants("FileVersion").Single().Value);
        Assert.Equal("$(Version)", document.Descendants("InformationalVersion").Single().Value);
        Assert.StartsWith(declared, typeof(ApplicationVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void UiUsesBackendVersionWithoutReleaseLiteral()
    {
        var ui = Path.Combine(RepositoryRoot(), "src", "IPTVExplorer.Desktop", "ui");
        var html = File.ReadAllText(Path.Combine(ui, "index.html"));
        var app = File.ReadAllText(Path.Combine(ui, "app.js"));
        var polish = File.ReadAllText(Path.Combine(ui, "v1-polish.js"));

        Assert.DoesNotMatch(@"Desktop · \d+\.\d+\.\d+", html);
        Assert.DoesNotMatch(@"Desktop · \d+\.\d+\.\d+", polish);
        Assert.Contains("state.app.version", app, StringComparison.Ordinal);
        Assert.Contains("Desktop · ${state.app.version}", app, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v1.0.0", false)]
    [InlineData("v1.1.0", false)]
    [InlineData("v1.1.1", true)]
    [InlineData("v1.2.0", true)]
    [InlineData("v2.0.0", true)]
    [InlineData("not-a-version", false)]
    public void UpdateComparisonUsesRelease110AssemblyVersion(string releaseTag, bool expected)
    {
        Assert.Equal("1.1.0", ApplicationVersion.Display);
        Assert.Equal(expected, ApplicationVersion.IsNewerRelease(releaseTag));
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
