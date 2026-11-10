using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Eager pre-grab unmonitor (#26): a monitored single with no downloaded files whose every
/// catalog track is Tier-1/Tier-2 redundant against downloaded library albums is unmonitored
/// at the album lifecycle point where it became grabbable (metadata refresh, add, refresh,
/// monitoring edit) — before any grab — while every cleanup-path protection stays intact:
/// #17 whole-release protection (any unmatched catalog track, downloaded or not, keeps the
/// single monitored), Tier-3 FlagOnly semantics, the cleanup comparison-pool selection, and
/// no interference with releases that have downloaded content.
/// </summary>
public class EagerSingleUnmonitorServiceTests
{
    // ── Metadata refresh: the production churn scenario from #26 ─────────────

    [Fact]
    public void AlbumInfoRefreshedEvent_FullyTier1RedundantSingleNoFiles_IsUnmonitoredBeforeAnyGrab()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, trackService, historyRecorder, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.False(single.Monitored);
        Assert.Equal(new[] { (single.Id, false) }, albumService.SetMonitoredCalls);
        Assert.Single(historyRecorder.Unmonitors);
        Assert.Contains("trigger=metadata-refresh", historyRecorder.Unmonitors[0].Reason);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_Tier2TitleDurationMatchWithoutMbids_IsUnmonitored()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, historyRecorder, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "album-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 181000, null, fileId: 0) }); // no MBID, 1s apart

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.False(single.Monitored);
        Assert.Equal(new[] { (single.Id, false) }, albumService.SetMonitoredCalls);
        Assert.Single(historyRecorder.Unmonitors);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_SingleWithExclusiveUndownloadedBside_StaysMonitored()
    {
        // #17 whole-release protection: the redundant downloaded A-side must not carry an
        // exclusive undownloaded B-side into an unmonitor.
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[]
            {
                Track("Hit", 180000, "shared-recording-mbid", fileId: 0),
                Track("Exclusive B-Side", 200000, "exclusive-mbid", fileId: 0),
            });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_Tier3TitleOnlyDurationMismatch_StaysMonitored()
    {
        // FlagOnly semantics: title-only evidence never authorizes the eager unmonitor.
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "album-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 195000, "single-recording-mbid", fileId: 0) }); // 15s apart

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_SingleHasDownloadedContent_IsNotTouched()
    {
        // Releases with content stay owned by the import-time/scheduled cleanup paths,
        // whose unmonitor is followed by file deletion.
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 21) }); // has a file

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_AddedNonSingleAndAlreadyUnmonitored_Ignored()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var addedEp = new Album { Id = 300, Title = "Some EP", AlbumType = "EP", Monitored = true };
        var unmonitoredSingle = Single(201, "Old Single", monitored: false);
        var redundantSingle = Single(202, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist,
            album,
            redundantSingle,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) },
            extraAlbums: new[] { unmonitoredSingle, addedEp });

        service.Handle(new AlbumInfoRefreshedEvent(
            artist,
            new List<Album> { addedEp, unmonitoredSingle, redundantSingle },
            new List<Album>(),
            new List<Album>()));

        Assert.False(redundantSingle.Monitored); // only the redundant monitored single acted on
        Assert.False(unmonitoredSingle.Monitored);
        Assert.True(addedEp.Monitored);
        Assert.Equal(new[] { (redundantSingle.Id, false) }, albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_MixedAddedSingles_OnlyFullyRedundantOnesUnmonitored()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var redundant = Single(203, "Redundant Single", monitored: true);
        var exclusive = Single(204, "Exclusive Single", monitored: true);
        var (albumService, trackService, _, service) = Setup(
            artist,
            album,
            redundant,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) },
            extraAlbums: new[] { exclusive });
        trackService.WithTracks(exclusive.Id, Track("Unreleased", 200000, "exclusive-mbid", fileId: 0));

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { redundant, exclusive }, new List<Album>(), new List<Album>()));

        Assert.False(redundant.Monitored);
        Assert.True(exclusive.Monitored);
        Assert.Equal(new[] { (redundant.Id, false) }, albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_EmptyOrNullAdded_IsNoOp()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, trackService, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album>(), new List<Album>(), new List<Album>()));
        service.Handle(new AlbumInfoRefreshedEvent(null!, new List<Album> { single }, new List<Album>(), new List<Album>()));
        service.Handle((AlbumInfoRefreshedEvent)null!);

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(trackService.AlbumLookupIds);
    }

    // ── Comparison pool mirrors the cleanup path selection ──────────────────

    [Fact]
    public void AlbumInfoRefreshedEvent_UnmonitoredComparisonAlbum_IsNotAComparisonSource()
    {
        var artist = Artist();
        var unmonitoredAlbum = DownloadedAlbum(monitored: false);
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist, unmonitoredAlbum, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_LiveSecondaryTypeComparisonAlbum_IsNotAComparisonSource()
    {
        var artist = Artist();
        var liveAlbum = DownloadedAlbum();
        liveAlbum.SecondaryTypes = new List<SecondaryAlbumType> { SecondaryAlbumType.Live };
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist, liveAlbum, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_UndownloadedComparisonAlbum_IsNotAComparisonSource()
    {
        // Only downloaded comparison tracks count (R012) — an undownloaded album with the
        // same track is not evidence the single is redundant.
        var artist = Artist();
        var album = DownloadedAlbum(); // no tracks with files below
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumInfoRefreshedEvent_TrackLookupFailure_FailsSafeKeepsMonitored()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, trackService, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });
        trackService.ThrowOnGetTracksByAlbum = true;

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    // ── Manual add / album refresh / monitoring edit paths ──────────────────

    [Fact]
    public void AlbumAddedEvent_RedundantSingleWithTracks_IsUnmonitored()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        single.Artist = artist;
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumAddedEvent(single));

        Assert.False(single.Monitored);
        Assert.Equal(new[] { (single.Id, false) }, albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumAddedEvent_WithoutTracksYet_FailsSafeKeepsMonitored()
    {
        // Manual add typically publishes AlbumAddedEvent before the album refresh inserts
        // track rows; the follow-up refresh re-evaluates with tracks present.
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        single.Artist = artist;
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: Array.Empty<Track>());

        service.Handle(new AlbumAddedEvent(single));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumUpdatedEvent_RedundantSingle_IsUnmonitored()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        single.Artist = artist;
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumUpdatedEvent(single));

        Assert.False(single.Monitored);
        Assert.Equal(new[] { (single.Id, false) }, albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumEditedEvent_MonitoringToggledOnRedundantSingle_IsUnmonitoredWithoutRecursion()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        single.Artist = artist;
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumEditedEvent(single, single));

        Assert.False(single.Monitored);
        Assert.Equal(new[] { (single.Id, false) }, albumService.SetMonitoredCalls);

        // The unmonitor publishes AlbumEditedEvent with the persisted unmonitored state;
        // the re-entrant dispatch must terminate immediately.
        service.Handle(new AlbumEditedEvent(single, single));
        Assert.Single(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void AlbumAddedEvent_ArtistResolutionFailure_IsContained()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        // Album's ctor plants a placeholder Artist (Id 0); resolution falls back to the
        // persisted ArtistMetadataId, whose lookup fails here — both must stay contained.
        var single = Single(200, "Hit Single", monitored: true);
        single.ArtistMetadataId = 77;
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) },
            throwOnGetArtist: true);

        service.Handle(new AlbumAddedEvent(single));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    // ── Settings gate and precedence ─────────────────────────────────────────

    [Fact]
    public void SettingsGate_DisabledViaNotificationSettings_NeverUnmonitors()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) },
            notificationSettings: new UniqueSinglesSettings { EagerUnmonitorRedundantSingles = false });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));
        service.Handle(new AlbumAddedEvent(single));
        service.Handle(new AlbumUpdatedEvent(single));
        service.Handle(new AlbumEditedEvent(single, single));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void SettingsGate_MetadataSettingsUsed_WhenNoNotificationExists()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, _, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) },
            notificationSettings: null,
            metadataSettings: new UniqueSinglesSettings { EagerUnmonitorRedundantSingles = false });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void SettingsGate_DefaultSettings_EnableTheEagerPath()
    {
        var settings = new UniqueSinglesSettings();

        Assert.True(settings.EagerUnmonitorRedundantSingles);
        Assert.True(settings.ToCleanupOptions().EagerUnmonitorRedundantSingles);
    }

    [Fact]
    public void CleanupOptions_EagerFlag_SurvivesWithAudioCancellationCopy()
    {
        var options = new UniqueSinglesSettings { EagerUnmonitorRedundantSingles = false }.ToCleanupOptions();

        Assert.False(options.WithAudioCancellation(System.Threading.CancellationToken.None).EagerUnmonitorRedundantSingles);
    }

    // ── Failure containment ──────────────────────────────────────────────────

    [Fact]
    public void UnmonitorFailure_IsContainedAndSingleStaysMonitored()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, historyRecorder, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });
        albumService.ThrowOnSetAlbumMonitored = true;

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.True(single.Monitored);
        Assert.Empty(historyRecorder.Unmonitors);
    }

    [Fact]
    public void HistoryRecorderFailure_NeverAffectsTheUnmonitorDecision()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, _, historyRecorder, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });
        historyRecorder.ThrowOnRecord = true;

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        Assert.False(single.Monitored);
        Assert.Equal(new[] { (single.Id, false) }, albumService.SetMonitoredCalls);
    }

    // ── The eager path can never trigger a search or grab ────────────────────

    [Fact]
    public void EagerService_HasNoSearchGrabDownloadOrDeleteDependencies()
    {
        var constructor = typeof(EagerSingleUnmonitorService).GetConstructors().Single();

        foreach (var parameter in constructor.GetParameters())
        {
            var typeNames = new[] { parameter.ParameterType.Name }
                .Concat(parameter.ParameterType.GetInterfaces().Select(i => i.Name));

            Assert.All(typeNames, name =>
                Assert.False(
                    name.Contains("Command", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Search", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Grab", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Download", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("DeleteMediaFiles", StringComparison.OrdinalIgnoreCase),
                    $"Eager path must not depend on {parameter.ParameterType.Name}: it may never trigger or influence a search, grab, download, or file deletion."));
        }
    }

    [Fact]
    public void EagerPath_PerformsOnlyReadOnlyAndUnmonitorCalls()
    {
        var artist = Artist();
        var album = DownloadedAlbum();
        var single = Single(200, "Hit Single", monitored: true);
        var (albumService, trackService, historyRecorder, service) = Setup(
            artist, album, single,
            albumTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 11) },
            singleTracks: new[] { Track("Hit", 180000, "shared-recording-mbid", fileId: 0) });

        service.Handle(new AlbumInfoRefreshedEvent(artist, new List<Album> { single }, new List<Album>(), new List<Album>()));

        // The only state-changing calls across every double are the single unmonitor and
        // its audit row. Lookups are reads; nothing else was invoked (doubles throw on
        // unused mutating members).
        Assert.Equal(new[] { (single.Id, false) }, albumService.SetMonitoredCalls);
        Assert.Empty(albumService.AllLibraryLookupCalls); // no GetAlbum/GetArtistAlbumsWithFiles calls
        Assert.Equal(new[] { single.Id, album.Id }, trackService.AlbumLookupIds); // candidate + comparison reads only
        Assert.Single(historyRecorder.Unmonitors);
    }

    // ── Test seams ───────────────────────────────────────────────────────────

    private static (RecordingAlbumService AlbumService, RecordingTrackService TrackService, RecordingHistoryRecorder HistoryRecorder, EagerSingleUnmonitorService Service) Setup(
        Artist artist,
        Album comparisonAlbum,
        Album single,
        Track[] albumTracks,
        Track[] singleTracks,
        Album[]? extraAlbums = null,
        UniqueSinglesSettings? notificationSettings = null,
        UniqueSinglesSettings? metadataSettings = null,
        bool throwOnGetArtist = false)
    {
        var albums = new List<Album> { comparisonAlbum, single }.Concat(extraAlbums ?? Array.Empty<Album>()).ToArray();
        var albumService = new RecordingAlbumService(albums);
        var trackService = new RecordingTrackService()
            .WithTracks(comparisonAlbum.Id, albumTracks)
            .WithTracks(single.Id, singleTracks);
        var historyRecorder = new RecordingHistoryRecorder();

        var notificationDefinitions = notificationSettings == null
            ? new List<NotificationDefinition>()
            : new List<NotificationDefinition> { new() { Settings = notificationSettings } };
        var metadataDefinitions = metadataSettings == null
            ? new List<MetadataDefinition>()
            : new List<MetadataDefinition> { new() { Implementation = nameof(UniqueSinglesScanTask), Settings = metadataSettings } };

        var service = CreateService(albumService, trackService, historyRecorder, artist, notificationDefinitions, metadataDefinitions, throwOnGetArtist);
        return (albumService, trackService, historyRecorder, service);
    }

    private static EagerSingleUnmonitorService CreateService(
        RecordingAlbumService albumService,
        RecordingTrackService trackService,
        RecordingHistoryRecorder historyRecorder,
        Artist artist,
        List<NotificationDefinition> notificationDefinitions,
        List<MetadataDefinition> metadataDefinitions,
        bool throwOnGetArtist = false)
    {
        var logger = new TestLogger();
        var notificationFactory = new StubNotificationFactory(notificationDefinitions);
        var metadataFactory = DispatchProxy.Create<IMetadataFactory, MetadataFactoryProxy>();
        ((MetadataFactoryProxy)(object)metadataFactory).Definitions = metadataDefinitions;

        return new EagerSingleUnmonitorService(
            albumService,
            trackService,
            new LookupArtistService(artist, throwOnGetArtist),
            historyRecorder,
            new Lazy<INotificationFactory>(() => notificationFactory),
            new Lazy<IMetadataFactory>(() => metadataFactory),
            logger.Logger);
    }

    private static Artist Artist() => new() { Id = 42, Name = "Kim Petras", Monitored = true };

    private static Album DownloadedAlbum(bool monitored = true) => new()
    {
        Id = 100,
        Title = "Feed the Beast",
        AlbumType = "Album",
        Monitored = monitored,
    };

    private static Album Single(int id, string title, bool monitored) => new()
    {
        Id = id,
        Title = title,
        AlbumType = "Single",
        Monitored = monitored,
    };

    private static Track Track(string title, int duration, string? mbid, int fileId) => new()
    {
        Title = title,
        Duration = duration,
        ForeignRecordingId = mbid,
        TrackFileId = fileId,
    };

    public class MetadataFactoryProxy : DispatchProxy
    {
        public List<MetadataDefinition> Definitions { get; set; } = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method!.Name == "All" ? Definitions : throw new NotSupportedException(method.Name);
        }
    }

    private sealed class LookupArtistService : IArtistService
    {
        private readonly Artist? _artist;
        private readonly bool _throwOnLookup;

        public LookupArtistService(Artist? artist, bool throwOnLookup = false)
        {
            _artist = artist;
            _throwOnLookup = throwOnLookup;
        }

        public Artist GetArtist(int artistId)
        {
            if (_throwOnLookup || _artist == null || _artist.Id != artistId)
            {
                throw new InvalidOperationException($"Artist {artistId} not found");
            }

            return _artist;
        }

        public Artist GetArtistByMetadataId(int artistMetadataId)
        {
            if (_throwOnLookup)
            {
                throw new InvalidOperationException($"Artist for metadata {artistMetadataId} not found");
            }

            return GetArtist(_artist!.Id);
        }

        public List<Artist> GetAllArtists() => _artist == null ? new List<Artist>() : new List<Artist> { _artist };

        // Unused IArtistService members — throw for safety
        public List<Artist> GetArtists(IEnumerable<int> artistIds) => NotUsed<List<Artist>>();
        public Artist AddArtist(Artist newArtist, bool doRefresh) => NotUsed<Artist>();
        public List<Artist> AddArtists(List<Artist> newArtists, bool doRefresh) => NotUsed<List<Artist>>();
        public Artist FindById(string foreignArtistId) => NotUsed<Artist>();
        public Artist FindByName(string title) => NotUsed<Artist>();
        public Artist FindByNameInexact(string title) => NotUsed<Artist>();
        public List<Artist> GetCandidates(string title) => NotUsed<List<Artist>>();
        public void DeleteArtist(int artistId, bool deleteFiles, bool addImportListExclusion = false) => NotUsed();
        public void DeleteArtists(List<int> artistIds, bool deleteFiles, bool addImportListExclusion = false) => NotUsed();
        public Dictionary<int, List<int>> GetAllArtistsTags() => NotUsed<Dictionary<int, List<int>>>();
        public List<Artist> AllForTag(int tagId) => NotUsed<List<Artist>>();
        public Artist UpdateArtist(Artist artist, bool publishUpdatedEvent = true) => NotUsed<Artist>();
        public List<Artist> UpdateArtists(List<Artist> artist, bool useExistingRelativeFolder) => NotUsed<List<Artist>>();
        public Dictionary<int, string> AllArtistPaths() => NotUsed<Dictionary<int, string>>();
        public bool ArtistPathExists(string folder) => NotUsed<bool>();
        public void RemoveAddOptions(Artist artist) => NotUsed();

        private static T NotUsed<T>() => throw new NotSupportedException("Not used in focused eager unmonitor tests.");
        private static void NotUsed() => throw new NotSupportedException("Not used in focused eager unmonitor tests.");
    }

    private sealed class RecordingAlbumService : IAlbumService
    {
        private readonly List<Album> _albums;

        public RecordingAlbumService(params Album[] albums)
        {
            _albums = albums.ToList();
        }

        public bool ThrowOnSetAlbumMonitored { get; set; }
        public List<int> ArtistAlbumLookupIds { get; } = new();
        public List<int> AllLibraryLookupCalls { get; } = new();
        public List<(int AlbumId, bool Monitored)> SetMonitoredCalls { get; } = new();

        public Album GetAlbum(int albumId)
        {
            AllLibraryLookupCalls.Add(albumId);
            return _albums.Single(a => a.Id == albumId);
        }

        public List<Album> GetAlbums(IEnumerable<int> albumIds)
        {
            var ids = albumIds.ToHashSet();
            return _albums.Where(a => ids.Contains(a.Id)).ToList();
        }

        public List<Album> GetAlbumsByArtist(int artistId)
        {
            ArtistAlbumLookupIds.Add(artistId);
            return _albums;
        }

        public List<Album> GetNextAlbumsByArtistMetadataId(IEnumerable<int> artistMetadataIds) => NotUsed<List<Album>>();
        public List<Album> GetLastAlbumsByArtistMetadataId(IEnumerable<int> artistMetadataIds) => NotUsed<List<Album>>();
        public List<Album> GetAlbumsByArtistMetadataId(int artistMetadataId) => NotUsed<List<Album>>();
        public List<Album> GetAlbumsForRefresh(int artistMetadataId, List<string> foreignIds) => NotUsed<List<Album>>();
        public Album AddAlbum(Album newAlbum, bool doRefresh) => NotUsed<Album>();
        public Album FindById(string foreignId) => NotUsed<Album>();
        public Album FindByTitle(int artistMetadataId, string title) => NotUsed<Album>();
        public Album FindByTitleInexact(int artistMetadataId, string title) => NotUsed<Album>();
        public List<Album> GetCandidates(int artistMetadataId, string title) => NotUsed<List<Album>>();
        public void DeleteAlbum(int albumId, bool deleteFiles, bool addImportListExclusion = false) => NotUsed();
        public List<Album> GetAllAlbums() => _albums.ToList();
        public Album UpdateAlbum(Album album) => NotUsed<Album>();

        public void SetAlbumMonitored(int albumId, bool monitored)
        {
            SetMonitoredCalls.Add((albumId, monitored));
            if (ThrowOnSetAlbumMonitored)
            {
                throw new InvalidOperationException("unmonitor failed");
            }
        }

        public void SetMonitored(IEnumerable<int> ids, bool monitored)
        {
            foreach (var id in ids)
            {
                SetAlbumMonitored(id, monitored);
            }
        }

        public void UpdateLastSearchTime(List<Album> albums) => NotUsed();
        public NzbDrone.Core.Datastore.PagingSpec<Album> AlbumsWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Album> pagingSpec) => NotUsed<NzbDrone.Core.Datastore.PagingSpec<Album>>();
        public List<Album> AlbumsBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => NotUsed<List<Album>>();
        public List<Album> ArtistAlbumsBetweenDates(Artist artist, DateTime start, DateTime end, bool includeUnmonitored) => NotUsed<List<Album>>();
        public void InsertMany(List<Album> albums) => NotUsed();
        public void UpdateMany(List<Album> albums) => NotUsed();
        public void DeleteMany(List<Album> albums) => NotUsed();
        public void SetAddOptions(IEnumerable<Album> albums) => NotUsed();
        public Album FindAlbumByRelease(string albumReleaseId) => NotUsed<Album>();
        public Album FindAlbumByTrackId(int trackId) => NotUsed<Album>();

        public List<Album> GetArtistAlbumsWithFiles(Artist artist)
        {
            AllLibraryLookupCalls.Add(artist.Id);
            return _albums;
        }

        private static T NotUsed<T>() => throw new NotSupportedException("Not used in focused eager unmonitor tests.");
        private static void NotUsed() => throw new NotSupportedException("Not used in focused eager unmonitor tests.");
    }

    private sealed class RecordingTrackService : ITrackService
    {
        private readonly Dictionary<int, List<Track>> _tracksByAlbum = new();

        public bool ThrowOnGetTracksByAlbum { get; set; }
        public List<int> AlbumLookupIds { get; } = new();
        public List<int> ArtistLookupIds { get; } = new();

        public RecordingTrackService WithTracks(int albumId, params Track[] tracks)
        {
            _tracksByAlbum[albumId] = tracks.ToList();
            return this;
        }

        public Track GetTrack(int id) => NotUsed<Track>();
        public List<Track> GetTracks(IEnumerable<int> ids) => NotUsed<List<Track>>();

        public List<Track> GetTracksByArtist(int artistId)
        {
            ArtistLookupIds.Add(artistId);
            return _tracksByAlbum.Values.SelectMany(t => t).ToList();
        }

        public List<Track> GetTracksByAlbum(int albumId)
        {
            if (ThrowOnGetTracksByAlbum)
            {
                throw new InvalidOperationException("track lookup failed");
            }

            AlbumLookupIds.Add(albumId);
            return _tracksByAlbum.TryGetValue(albumId, out var tracks) ? tracks : new List<Track>();
        }

        public List<Track> GetTracksByRelease(int albumReleaseId) => NotUsed<List<Track>>();
        public List<Track> GetTracksByReleases(List<int> albumReleaseIds) => NotUsed<List<Track>>();
        public List<Track> GetTracksForRefresh(int albumReleaseId, List<string> foreignTrackIds) => NotUsed<List<Track>>();
        public List<Track> TracksWithFiles(int artistId) => _tracksByAlbum.Values.SelectMany(t => t).Where(t => t.HasFile).ToList();
        public List<Track> TracksWithoutFiles(int albumId) => GetTracksByAlbum(albumId).Where(t => !t.HasFile).ToList();
        public List<Track> GetTracksByFileId(int trackFileId) => NotUsed<List<Track>>();
        public List<Track> GetTracksByFileId(IEnumerable<int> trackFileIds) => NotUsed<List<Track>>();
        public void UpdateTrack(Track track) => NotUsed();
        public void InsertMany(List<Track> tracks) => NotUsed();
        public void UpdateMany(List<Track> tracks) => NotUsed();
        public void DeleteMany(List<Track> tracks) => NotUsed();
        public void SetFileIds(List<Track> tracks) => NotUsed();

        private static T NotUsed<T>() => throw new NotSupportedException("Not used in focused eager unmonitor tests.");
        private static void NotUsed() => throw new NotSupportedException("Not used in focused eager unmonitor tests.");
    }

    private sealed class RecordingHistoryRecorder : ICleanupHistoryRecorder
    {
        public bool ThrowOnRecord { get; set; }
        public List<(int ArtistId, int AlbumId, string Reason)> Unmonitors { get; } = new();

        public void RecordUnmonitor(Artist artist, Album single, string reason)
        {
            if (ThrowOnRecord)
            {
                throw new InvalidOperationException("history write failed");
            }

            Unmonitors.Add((artist.Id, single.Id, reason));
        }
    }

    private sealed class StubNotificationFactory : INotificationFactory
    {
        private readonly List<NotificationDefinition> _definitions;

        public StubNotificationFactory(List<NotificationDefinition> definitions) => _definitions = definitions;

        public List<NotificationDefinition> All() => _definitions;

        // Unused INotificationFactory members — throw for safety
        public List<INotification> GetAvailableProviders() => NotUsed<List<INotification>>();
        public bool Exists(int id) => NotUsed<bool>();
        public NotificationDefinition Find(int id) => NotUsed<NotificationDefinition>();
        public NotificationDefinition Get(int id) => NotUsed<NotificationDefinition>();
        public IEnumerable<NotificationDefinition> Get(IEnumerable<int> ids) => NotUsed<IEnumerable<NotificationDefinition>>();
        public NotificationDefinition Create(NotificationDefinition definition) => NotUsed<NotificationDefinition>();
        public void Update(NotificationDefinition definition) => NotUsed();
        public IEnumerable<NotificationDefinition> Update(IEnumerable<NotificationDefinition> definitions) => NotUsed<IEnumerable<NotificationDefinition>>();
        public void Delete(int id) => NotUsed();
        public void Delete(IEnumerable<int> ids) => NotUsed();
        public IEnumerable<NotificationDefinition> GetDefaultDefinitions() => NotUsed<IEnumerable<NotificationDefinition>>();
        public IEnumerable<NotificationDefinition> GetPresetDefinitions(NotificationDefinition providerDefinition) => NotUsed<IEnumerable<NotificationDefinition>>();
        public void SetProviderCharacteristics(NotificationDefinition definition) => NotUsed();
        public void SetProviderCharacteristics(INotification provider, NotificationDefinition definition) => NotUsed();
        public INotification GetInstance(NotificationDefinition definition) => NotUsed<INotification>();
        public FluentValidation.Results.ValidationResult Test(NotificationDefinition definition) => NotUsed<FluentValidation.Results.ValidationResult>();
        public object RequestAction(NotificationDefinition definition, string action, IDictionary<string, string> query) => NotUsed<object>();
        public List<NotificationDefinition> AllForTag(int tagId) => NotUsed<List<NotificationDefinition>>();
        public List<INotification> OnGrabEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnReleaseImportEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnUpgradeEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnRenameEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnArtistAddEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnArtistDeleteEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnAlbumDeleteEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnHealthIssueEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnHealthRestoredEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnDownloadFailureEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnImportFailureEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnTrackRetagEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();
        public List<INotification> OnApplicationUpdateEnabled(bool filterBlockedNotifications = true) => NotUsed<List<INotification>>();

        private static T NotUsed<T>() => throw new NotSupportedException("Not used in focused eager unmonitor tests.");
        private static void NotUsed() => throw new NotSupportedException("Not used in focused eager unmonitor tests.");
    }

    private sealed class TestLogger : IDisposable
    {
        private readonly LogFactory _factory;
        private readonly MemoryTarget _target;

        public TestLogger()
        {
            _factory = new LogFactory();
            _target = new MemoryTarget { Layout = "${level}|${message}" };
            var config = new LoggingConfiguration();
            config.AddRuleForAllLevels(_target);
            _factory.Configuration = config;
            Logger = _factory.GetLogger(Guid.NewGuid().ToString("N"));
        }

        public Logger Logger { get; }

        public bool HasMessageContaining(string substring)
        {
            _factory.Flush();
            return _target.Logs.Any(log => log.Contains(substring, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            _factory.Flush();
            _factory.Dispose();
        }
    }
}
