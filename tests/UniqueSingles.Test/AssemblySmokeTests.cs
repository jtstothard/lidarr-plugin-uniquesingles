using System.Reflection;
using Xunit;
using NzbDrone.Core.Messaging.Commands;

namespace UniqueSingles.Test;

/// <summary>
/// Reflection-based smoke test to verify plugin assembly contains required types.
/// Confirms the command and executor contract is satisfied without Lidarr runtime.
/// </summary>
public class AssemblySmokeTests
{
    [Fact]
    public void Assembly_ContainsCommandSubclass()
    {
        var assembly = typeof(UniqueSingles.Commands.UniqueSinglesScanCommand).Assembly;

        // Find all types that inherit from Command
        var commandTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(Command).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(commandTypes);
        Assert.Contains(commandTypes, t => t.Name.Contains("ScanCommand", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Assembly_ContainsExecuteInterfaceImplementation()
    {
        var assembly = typeof(UniqueSingles.Commands.UniqueSinglesScanCommand).Assembly;

        // Find the command type
        var commandType = assembly.GetTypes()
            .FirstOrDefault(t => t.Name == "UniqueSinglesScanCommand");

        Assert.NotNull(commandType);

        // Find all types that implement IExecute<TCommand>
        var executeInterfaceType = typeof(IExecute<>);
        var executorTypes = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces()
                .Any(i => i.IsGenericType &&
                        i.GetGenericTypeDefinition() == executeInterfaceType &&
                        i.GetGenericArguments()[0] == commandType))
            .ToList();

        Assert.NotEmpty(executorTypes);
        Assert.Contains(executorTypes, t => t.Name.Contains("UniqueSinglesScanCommandService", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Assembly_ContainsRequiredLidarrCompatTypes()
    {
        var assembly = typeof(UniqueSingles.Commands.UniqueSinglesScanCommand).Assembly;

        // Verify essential compatibility types exist
        var requiredTypes = new[]
        {
            "Command",
            "IExecute`1",
            "CompletionStatus",
            "IArtistService",
            "IAlbumService",
            "ITrackService",
            "IMediaFileService",
            "IDeleteMediaFiles",
            "Logger",
        };

        foreach (var typeName in requiredTypes)
        {
            var found = assembly.GetTypes()
                .Any(t => t.Name == typeName || t.Name == typeName.Replace("`1", ""));

            Assert.True(found, $"Required type '{typeName}' not found in assembly");
        }
    }
}