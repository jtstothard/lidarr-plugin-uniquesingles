using System;
using System.Collections.Generic;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Plugins.Scheduling
{
    /// <summary>
    /// Base class for metadata providers that also register a scheduled task.
    /// Inherits MetadataBase to plug into Lidarr's provider infrastructure
    /// (appears in Settings > Metadata). The no-op metadata method overrides
    /// ensure the provider compiles without requiring actual metadata logic.
    ///
    /// Subclasses must override CommandType, IntervalMinutes, and Priority.
    /// The ScheduledTaskService detects providers implementing IProvideScheduledTask
    /// and manages their ScheduledTask rows automatically.
    /// </summary>
    public abstract class ScheduledTaskBase<TSettings> : MetadataBase<TSettings>, IProvideScheduledTask
        where TSettings : IProviderConfig, new()
    {
        /// <inheritdoc />
        public abstract Type CommandType { get; }

        /// <inheritdoc />
        public abstract int IntervalMinutes { get; }

        /// <inheritdoc />
        public abstract CommandPriority Priority { get; }

        // No-op metadata overrides — this provider exists for scheduling, not metadata.

        public override MetadataFile FindMetadataFile(Artist artist, string path) => null!;

        public override MetadataFileResult ArtistMetadata(Artist artist) => null!;

        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null!;

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null!;

        public override List<ImageFileResult> ArtistImages(Artist artist) => new();

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
    }
}
