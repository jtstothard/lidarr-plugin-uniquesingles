using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DryIoc;
using Lidarr.Http.ClientSchema;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

public class UniqueSinglesSettingsSchemaTests
{
    public UniqueSinglesSettingsSchemaTests()
    {
        var container = new Container();
        container.RegisterInstance<ILocalizationService>(new PassthroughLocalizationService());
        SchemaBuilder.Initialize(container);
    }

    [Fact]
    public void Defaults_UseSafeAlbumEpComparisonBaseline()
    {
        var settings = new UniqueSinglesSettings();

        Assert.Equal(3000, settings.DurationToleranceMs);
        Assert.Equal("Album, EP", settings.ReleaseTypesToCheck);
        Assert.Equal(Tier3Action.FlagOnly, settings.Tier3Action);

        var options = settings.ToCleanupOptions();

        Assert.True(options.ShouldCompareAgainstType("Album"));
        Assert.True(options.ShouldCompareAgainstType("EP"));
        Assert.False(options.ShouldCompareAgainstType("Single"));
        Assert.Equal(Tier3Action.FlagOnly, options.Tier3Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void ToCleanupOptions_EmptyOrMalformedReleaseTypes_FallsBackToAlbumEp(string? releaseTypesToCheck)
    {
        var settings = new UniqueSinglesSettings
        {
            ReleaseTypesToCheck = releaseTypesToCheck!,
        };

        var options = settings.ToCleanupOptions();

        Assert.True(options.ShouldCompareAgainstType("Album"));
        Assert.True(options.ShouldCompareAgainstType("EP"));
        Assert.False(options.ShouldCompareAgainstType("Single"));
    }

    [Fact]
    public void SchemaBuilder_ProvidesTier3SelectOptions_AndCanReadSettingsBack()
    {
        var settings = new UniqueSinglesSettings();

        var schema = SchemaBuilder.ToSchema(settings);
        var tier3Field = Assert.Single(schema.Where(field => field.Name == "tier3Action"));

        Assert.Equal("select", tier3Field.Type);
        Assert.Null(tier3Field.SelectOptionsProviderAction);
        Assert.NotNull(tier3Field.SelectOptions);
        Assert.Collection(
            tier3Field.SelectOptions!.OrderBy(option => option.Value),
            option =>
            {
                Assert.Equal((int)Tier3Action.FlagOnly, option.Value);
                Assert.Equal("Flag for review", option.Name);
            },
            option =>
            {
                Assert.Equal((int)Tier3Action.Skip, option.Value);
                Assert.Equal("Skip cleanup", option.Name);
            });

        var roundTripped = Assert.IsType<UniqueSinglesSettings>(SchemaBuilder.ReadFromSchema(
            new List<Field>
            {
                new() { Name = "durationToleranceMs", Value = JsonDocument.Parse("4500").RootElement.Clone() },
                new() { Name = "releaseTypesToCheck", Value = JsonDocument.Parse("\"Album, EP\"").RootElement.Clone() },
                new() { Name = "tier3Action", Value = JsonDocument.Parse("1").RootElement.Clone() },
            },
            typeof(UniqueSinglesSettings),
            settings));

        Assert.Equal(4500, roundTripped.DurationToleranceMs);
        Assert.Equal("Album, EP", roundTripped.ReleaseTypesToCheck);
        Assert.Equal(Tier3Action.Skip, roundTripped.Tier3Action);
    }

    private sealed class PassthroughLocalizationService : ILocalizationService
    {
        public Dictionary<string, string> GetLocalizationDictionary()
        {
            return new Dictionary<string, string>();
        }

        public string GetLocalizedString(string phrase)
        {
            return phrase;
        }

        public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
        {
            return phrase;
        }
    }
}
