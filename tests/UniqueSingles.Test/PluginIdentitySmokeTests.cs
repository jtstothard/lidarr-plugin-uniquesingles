using System.IO.Compression;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Narrow smoke coverage for Lidarr plugin discovery expectations: one concrete
/// exported plugin root with stable metadata, plus a release package layout that
/// extracts directly into the installed-plugin folder without an extra wrapper.
/// </summary>
public class PluginIdentitySmokeTests
{
    [Fact]
    public void Assembly_ExportsExactlyOneConcretePluginRoot_WithExpectedIdentityMetadata()
    {
        var assembly = typeof(UniqueSinglesPlugin).Assembly;

        var pluginRoots = assembly.GetExportedTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IPlugin).IsAssignableFrom(type))
            .ToList();

        Assert.True(
            pluginRoots.Count == 1,
            $"Expected exactly one concrete exported IPlugin implementation, but found {pluginRoots.Count}: {string.Join(", ", pluginRoots.Select(type => type.FullName))}");

        var pluginType = pluginRoots[0];
        Assert.Equal(typeof(UniqueSinglesPlugin), pluginType);

        var plugin = Assert.IsType<UniqueSinglesPlugin>(Activator.CreateInstance(pluginType));
        Assert.Equal("Unique Singles", plugin.Name);
        Assert.Equal("jtstothard", plugin.Owner);
        Assert.Equal("https://github.com/jtstothard/lidarr-plugin-uniquesingles", plugin.GithubUrl);
        Assert.Equal("Lidarr.Plugin.UniqueSingles", assembly.GetName().Name);
    }

    [Fact]
    public void ReleasePackage_ContainsTopLevelPluginAssembly_WithoutExtraWrapperDirectory()
    {
        var packagePath = Path.Combine(
            FindRepositoryRoot(),
            "_temp",
            "bin",
            "Release",
            "UniqueSingles",
            "Lidarr.Plugin.UniqueSingles.net8.0.zip");

        Assert.True(
            File.Exists(packagePath),
            $"Expected Release package at '{packagePath}'. Run 'dotnet build src/UniqueSingles/UniqueSingles.csproj -c Release' before executing this smoke test.");

        using var archive = ZipFile.OpenRead(packagePath);

        var fileEntries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToList();

        const string expectedPluginDll = "Lidarr.Plugin.UniqueSingles.dll";
        Assert.True(
            fileEntries.Contains(expectedPluginDll, StringComparer.OrdinalIgnoreCase),
            $"Expected package '{packagePath}' to contain top-level '{expectedPluginDll}'. Actual file entries: {string.Join(", ", fileEntries)}");

        var nestedPluginDllEntries = fileEntries
            .Where(entry => entry.EndsWith($"/{expectedPluginDll}", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            nestedPluginDllEntries.Count == 0,
            $"Expected '{expectedPluginDll}' to be stored at the zip root, but found nested entries: {string.Join(", ", nestedPluginDllEntries)}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var srcPath = Path.Combine(directory.FullName, "src", "UniqueSingles", "UniqueSingles.csproj");
            var testsPath = Path.Combine(directory.FullName, "tests", "UniqueSingles.Test", "UniqueSingles.Test.csproj");

            if (File.Exists(srcPath) && File.Exists(testsPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Unable to locate repository root from '{AppContext.BaseDirectory}'. Expected to find src/UniqueSingles/UniqueSingles.csproj and tests/UniqueSingles.Test/UniqueSingles.Test.csproj in an ancestor.");
    }
}
