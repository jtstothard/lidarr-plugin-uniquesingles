using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Reflection-based smoke tests to verify the plugin assembly exports the core
/// Lidarr integration contracts needed for discovery.
/// </summary>
public class AssemblySmokeTests
{
    private static readonly Version SupportedLidarrVersion = new(3, 1, 3, 4968);

    [Fact]
    public void Assembly_ContainsCommandSubclass()
    {
        var assembly = typeof(UniqueSinglesScanCommand).Assembly;

        var commandTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(Command).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(commandTypes);
        Assert.Contains(commandTypes, t => t.Name.Contains("ScanCommand", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Assembly_ContainsExecuteInterfaceImplementation()
    {
        var assembly = typeof(UniqueSinglesScanCommand).Assembly;

        var commandType = assembly.GetTypes()
            .FirstOrDefault(t => t.Name == "UniqueSinglesScanCommand");

        Assert.NotNull(commandType);

        var executeInterfaceType = typeof(IExecute<>);
        var executorTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces()
                .Any(i => i.IsGenericType &&
                          i.GetGenericTypeDefinition() == executeInterfaceType &&
                          i.GetGenericArguments()[0] == commandType))
            .ToList();

        Assert.NotEmpty(executorTypes);
        Assert.Contains(executorTypes, t => t.Name.Contains("UniqueSinglesScanTask", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Assembly_ExportsConcretePluginRootWithExpectedMetadata()
    {
        var assembly = typeof(UniqueSinglesScanCommand).Assembly;

        var pluginRoots = assembly.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IPlugin).IsAssignableFrom(t))
            .ToList();

        var pluginType = Assert.Single(pluginRoots);
        Assert.Equal(typeof(UniqueSinglesPlugin), pluginType);

        var plugin = Assert.IsType<UniqueSinglesPlugin>(Activator.CreateInstance(pluginType));
        Assert.Equal(UniqueSinglesPlugin.DisplayName, plugin.Name);
        Assert.Equal(UniqueSinglesPlugin.RepositoryOwner, plugin.Owner);
        Assert.Equal(UniqueSinglesPlugin.RepositoryUrl, plugin.GithubUrl);
        Assert.Equal("Lidarr.Plugin.UniqueSingles", assembly.GetName().Name);
    }

    [Fact]
    public void Assembly_ReferencesSupportedLidarrAssemblyVersions()
    {
        var assembly = typeof(UniqueSinglesScanCommand).Assembly;
        var references = assembly.GetReferencedAssemblies()
            .Where(reference => reference.Name is "Lidarr.Core" or "Lidarr.Common")
            .ToDictionary(reference => reference.Name!, reference => reference.Version);

        Assert.Equal(SupportedLidarrVersion, references["Lidarr.Core"]);
        Assert.Equal(SupportedLidarrVersion, references["Lidarr.Common"]);
        Assert.DoesNotContain(references.Values, version => version?.Major == 10);
    }
}
