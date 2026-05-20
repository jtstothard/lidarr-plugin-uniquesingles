using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UniqueSingles.Test")]

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Exported Lidarr plugin root for Unique Singles. The assembly name remains
/// Lidarr.Plugin.UniqueSingles via UniqueSingles.csproj.
/// </summary>
public sealed class UniqueSinglesPlugin : Plugin
{
    public const string DisplayName = "Unique Singles";
    public const string RepositoryOwner = "jtstothard";
    public const string RepositoryUrl = "https://github.com/jtstothard/lidarr-plugin-uniquesingles";

    public override string Name => DisplayName;

    public override string Owner => RepositoryOwner;

    public override string GithubUrl => RepositoryUrl;
}
