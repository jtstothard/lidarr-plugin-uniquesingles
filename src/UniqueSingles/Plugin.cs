using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UniqueSingles.Test")]

namespace UniqueSingles;

/// <summary>
/// Plugin marker and metadata constants. The assembly name remains Lidarr.Plugin.UniqueSingles via UniqueSingles.csproj.
/// </summary>
public static class Plugin
{
    public const string Name = "Unique Singles";
}
