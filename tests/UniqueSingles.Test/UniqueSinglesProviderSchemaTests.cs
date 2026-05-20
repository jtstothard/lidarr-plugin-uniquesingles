using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DryIoc;
using Lidarr.Api.V1.Notifications;
using Lidarr.Http.ClientSchema;
using NLog;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

public class UniqueSinglesProviderSchemaTests
{
    public UniqueSinglesProviderSchemaTests()
    {
        var container = new Container();
        container.RegisterInstance<ILocalizationService>(new PassthroughLocalizationService());
        SchemaBuilder.Initialize(container);
    }

    [Fact]
    public void NotificationProviderSchema_RoundTripsPersistedSettings_AndExposesTier3SelectOptions()
    {
        var notification = new UniqueSinglesNotification(new NoOpCleanupService(), CreateLogger());
        var mapper = new NotificationResourceMapper();
        var definition = new NotificationDefinition
        {
            Name = notification.Name,
            ImplementationName = notification.Name,
            Implementation = nameof(UniqueSinglesNotification),
            Settings = new UniqueSinglesSettings
            {
                DurationToleranceMs = 4500,
                ReleaseTypesToCheck = "Album, EP",
                Tier3Action = Tier3Action.Skip,
            },
            OnReleaseImport = true,
            OnUpgrade = true,
            SupportsOnReleaseImport = notification.SupportsOnReleaseImport,
            SupportsOnUpgrade = notification.SupportsOnUpgrade,
        };

        var resource = mapper.ToResource(definition);

        Assert.Equal(notification.Name, resource.Name);
        Assert.Equal(nameof(UniqueSinglesNotification), resource.Implementation);
        Assert.Equal(typeof(UniqueSinglesSettings).Name, resource.ConfigContract);
        Assert.True(resource.OnReleaseImport);
        Assert.True(resource.OnUpgrade);
        Assert.True(resource.SupportsOnReleaseImport);
        Assert.True(resource.SupportsOnUpgrade);
        Assert.False(resource.SupportsOnGrab);
        Assert.False(resource.SupportsOnRename);
        Assert.False(resource.SupportsOnArtistAdd);
        Assert.False(resource.SupportsOnArtistDelete);
        Assert.False(resource.SupportsOnAlbumDelete);
        Assert.False(resource.SupportsOnHealthIssue);
        Assert.False(resource.SupportsOnHealthRestored);
        Assert.False(resource.SupportsOnDownloadFailure);
        Assert.False(resource.SupportsOnImportFailure);
        Assert.False(resource.SupportsOnTrackRetag);
        Assert.False(resource.SupportsOnApplicationUpdate);

        var tier3Field = Assert.Single(resource.Fields.Where(field => field.Name == "tier3Action"));
        Assert.Equal("select", tier3Field.Type);
        Assert.Equal(Tier3Action.Skip, Assert.IsType<Tier3Action>(tier3Field.Value));
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

        var roundTripped = mapper.ToModel(resource, definition);
        var settings = Assert.IsType<UniqueSinglesSettings>(roundTripped.Settings);

        Assert.Equal(4500, settings.DurationToleranceMs);
        Assert.Equal("Album, EP", settings.ReleaseTypesToCheck);
        Assert.Equal(Tier3Action.Skip, settings.Tier3Action);
        Assert.True(settings.ToCleanupOptions().ShouldCompareAgainstType("Album"));
        Assert.True(settings.ToCleanupOptions().ShouldCompareAgainstType("EP"));
        Assert.False(settings.ToCleanupOptions().ShouldCompareAgainstType("Single"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void NotificationProviderSchema_MalformedOrEmptyReleaseTypes_FallBackToAlbumEp(string? releaseTypesToCheck)
    {
        var mapper = new NotificationResourceMapper();
        var resource = new NotificationResource
        {
            Name = UniqueSinglesPlugin.DisplayName,
            ImplementationName = UniqueSinglesPlugin.DisplayName,
            Implementation = nameof(UniqueSinglesNotification),
            ConfigContract = typeof(UniqueSinglesSettings).Name,
            Fields = new List<Field>
            {
                new() { Name = "durationToleranceMs", Value = JsonDocument.Parse("3000").RootElement.Clone() },
                new() { Name = "releaseTypesToCheck", Value = JsonRoot(releaseTypesToCheck) },
                new() { Name = "tier3Action", Value = JsonDocument.Parse("0").RootElement.Clone() },
            },
            OnReleaseImport = true,
            OnUpgrade = true,
            SupportsOnReleaseImport = true,
            SupportsOnUpgrade = true,
        };

        var roundTripped = mapper.ToModel(resource, new NotificationDefinition
        {
            Settings = new UniqueSinglesSettings(),
        });

        var settings = Assert.IsType<UniqueSinglesSettings>(roundTripped.Settings);
        var options = settings.ToCleanupOptions();

        Assert.True(options.ShouldCompareAgainstType("Album"));
        Assert.True(options.ShouldCompareAgainstType("EP"));
        Assert.False(options.ShouldCompareAgainstType("Single"));
        Assert.Equal(Tier3Action.FlagOnly, settings.Tier3Action);
    }

    private static JsonElement JsonRoot(string? value)
    {
        return JsonDocument.Parse(value is null ? "null" : JsonSerializer.Serialize(value)).RootElement.Clone();
    }

    private static Logger CreateLogger()
    {
        return new LogFactory().GetLogger($"{nameof(UniqueSinglesProviderSchemaTests)}.{Guid.NewGuid():N}");
    }

    private sealed class NoOpCleanupService : ISingleCleanupService
    {
        public void CleanupSinglesForArtist(NzbDrone.Core.Music.Artist artist, NzbDrone.Core.Music.Album importedAlbum)
        {
        }

        public void CleanupSingleSelfCheck(NzbDrone.Core.Music.Artist artist, NzbDrone.Core.Music.Album importedSingle)
        {
        }

        public CleanupResult CleanupWithOptions(NzbDrone.Core.Music.Artist artist, NzbDrone.Core.Music.Album importedAlbum, SingleCleanupOptions options)
        {
            return CleanupResult.Empty;
        }

        public CleanupResult ScanArtistWithOptions(NzbDrone.Core.Music.Artist artist, SingleCleanupOptions options)
        {
            return CleanupResult.Empty;
        }
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
