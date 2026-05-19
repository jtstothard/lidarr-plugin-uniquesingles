using Xunit;

namespace UniqueSingles.Test;

public class SingleCleanupOptionsTests
{
    [Fact]
    public void Constructor_ValidValues_PropertiesSetCorrectly()
    {
        var releaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" };
        var options = new SingleCleanupOptions(5000, releaseTypes, Tier3Action.FlagOnly);

        Assert.Equal(5000, options.DurationToleranceMs);
        Assert.Equal(2, options.ComparisonReleaseTypes.Count);
        Assert.True(options.ComparisonReleaseTypes.Contains("Album"));
        Assert.True(options.ComparisonReleaseTypes.Contains("EP"));
        Assert.Equal(Tier3Action.FlagOnly, options.Tier3Action);
    }

    [Fact]
    public void Constructor_NonPositiveDuration_ClampsTo3000()
    {
        var releaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" };

        Assert.Equal(3000, new SingleCleanupOptions(0, releaseTypes, Tier3Action.FlagOnly).DurationToleranceMs);
        Assert.Equal(3000, new SingleCleanupOptions(-100, releaseTypes, Tier3Action.FlagOnly).DurationToleranceMs);
    }

    [Fact]
    public void Constructor_DurationBelowMinimum_ClampsTo1000()
    {
        var releaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" };

        Assert.Equal(1000, new SingleCleanupOptions(500, releaseTypes, Tier3Action.FlagOnly).DurationToleranceMs);
        Assert.Equal(1000, new SingleCleanupOptions(999, releaseTypes, Tier3Action.FlagOnly).DurationToleranceMs);
    }

    [Fact]
    public void Constructor_DurationAboveMinimum_PreservesValue()
    {
        var releaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" };

        Assert.Equal(1500, new SingleCleanupOptions(1500, releaseTypes, Tier3Action.FlagOnly).DurationToleranceMs);
        Assert.Equal(3000, new SingleCleanupOptions(3000, releaseTypes, Tier3Action.FlagOnly).DurationToleranceMs);
        Assert.Equal(10000, new SingleCleanupOptions(10000, releaseTypes, Tier3Action.FlagOnly).DurationToleranceMs);
    }

    [Fact]
    public void Constructor_NullReleaseTypes_FallsBackToAlbumEp()
    {
        var options = new SingleCleanupOptions(5000, null, Tier3Action.FlagOnly);

        Assert.Equal(2, options.ComparisonReleaseTypes.Count);
        Assert.True(options.ComparisonReleaseTypes.Contains("Album"));
        Assert.True(options.ComparisonReleaseTypes.Contains("EP"));
    }

    [Fact]
    public void Constructor_EmptyReleaseTypes_FallsBackToAlbumEp()
    {
        var options = new SingleCleanupOptions(5000, new HashSet<string>(), Tier3Action.FlagOnly);

        Assert.Equal(2, options.ComparisonReleaseTypes.Count);
        Assert.True(options.ComparisonReleaseTypes.Contains("Album"));
        Assert.True(options.ComparisonReleaseTypes.Contains("EP"));
    }

    [Fact]
    public void ShouldCompareAgainstType_MatchingType_ReturnsTrue()
    {
        var releaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" };
        var options = new SingleCleanupOptions(5000, releaseTypes, Tier3Action.FlagOnly);

        Assert.True(options.ShouldCompareAgainstType("Album"));
        Assert.True(options.ShouldCompareAgainstType("EP"));
        Assert.True(options.ShouldCompareAgainstType("album")); // Case-insensitive
        Assert.True(options.ShouldCompareAgainstType("ALBUM")); // Case-insensitive
    }

    [Fact]
    public void ShouldCompareAgainstType_NonMatchingType_ReturnsFalse()
    {
        var releaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" };
        var options = new SingleCleanupOptions(5000, releaseTypes, Tier3Action.FlagOnly);

        Assert.False(options.ShouldCompareAgainstType("Single"));
        Assert.False(options.ShouldCompareAgainstType("Compilation"));
        Assert.False(options.ShouldCompareAgainstType("Live"));
    }

    [Fact]
    public void ShouldCompareAgainstType_NullOrEmptyType_ReturnsFalse()
    {
        var releaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" };
        var options = new SingleCleanupOptions(5000, releaseTypes, Tier3Action.FlagOnly);

        Assert.False(options.ShouldCompareAgainstType(null));
        Assert.False(options.ShouldCompareAgainstType(""));
        Assert.False(options.ShouldCompareAgainstType("   "));
    }

    [Fact]
    public void Constructor_MixedCaseReleaseTypes_PreservesCaseInsensitive()
    {
        var releaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "ep", "LIVE" };
        var options = new SingleCleanupOptions(5000, releaseTypes, Tier3Action.FlagOnly);

        Assert.Equal(3, options.ComparisonReleaseTypes.Count);
        Assert.True(options.ShouldCompareAgainstType("Album"));
        Assert.True(options.ShouldCompareAgainstType("album"));
        Assert.True(options.ShouldCompareAgainstType("EP"));
        Assert.True(options.ShouldCompareAgainstType("ep"));
        Assert.True(options.ShouldCompareAgainstType("LIVE"));
        Assert.True(options.ShouldCompareAgainstType("live"));
    }
}