namespace NzbDrone.Core.Annotations
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class FieldDefinitionAttribute : Attribute
    {
        public int Order { get; set; }
        public string Label { get; set; } = string.Empty;
        public string HelpText { get; set; } = string.Empty;
        public FieldType Type { get; set; } = FieldType.Textbox;
    }

    public enum FieldType
    {
        Textbox,
        Number,
        Select,
        Checkbox,
        Tag
    }
}

namespace NzbDrone.Core.ThingiProvider
{
    /// <summary>
    /// Minimal provider-config contract used when the Lidarr submodule is not present in this worktree.
    /// </summary>
    public interface IProviderConfig
    {
    }
}

namespace NzbDrone.Core.Music
{
    public class Artist
    {
        public int Id { get; set; }
        public int ArtistMetadataId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class Album
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string AlbumType { get; set; } = string.Empty;
        public bool Monitored { get; set; } = true;
        public int ArtistMetadataId { get; set; }
        public string ForeignAlbumId { get; set; } = string.Empty;
    }

    public class Track
    {
        public int Id { get; set; }
        public int AlbumId { get; set; }
        public int AlbumReleaseId { get; set; }
        public int ArtistMetadataId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ForeignRecordingId { get; set; } = string.Empty;
        public int Duration { get; set; }
        public int TrackFileId { get; set; }
        public bool HasFile => TrackFileId > 0;
    }

    public interface IAlbumService
    {
        List<Album> GetAlbumsByArtist(int artistId);
        Album GetAlbum(int albumId);
        List<Album> GetArtistAlbumsWithFiles(Artist artist);
        void SetAlbumMonitored(int albumId, bool monitored);
    }

    public interface ITrackService
    {
        List<Track> GetTracksByAlbum(int albumId);
        List<Track> GetTracksByArtist(int artistId);
    }

    /// <summary>
    /// Minimal artist service contract needed for full-library scan.
    /// </summary>
    public interface IArtistService
    {
        List<Artist> GetAllArtists();
        Artist? GetArtist(int artistId);
    }
}

namespace NzbDrone.Core.MediaFiles
{
    using NzbDrone.Core.Music;

    public class TrackFile
    {
        public int Id { get; set; }
        public int AlbumId { get; set; }
        public string Path { get; set; } = string.Empty;
    }

    public interface IMediaFileService
    {
        TrackFile Get(int id);
        List<TrackFile> GetFilesByAlbum(int albumId);
    }

    public interface IDeleteMediaFiles
    {
        void DeleteTrackFile(Artist artist, TrackFile trackFile);
    }
}

namespace NLog
{
    public class Logger
    {
        public virtual void Trace(string message, params object?[] args) { }
        public virtual void Debug(string message, params object?[] args) { }
        public virtual void Info(string message, params object?[] args) { }
        public virtual void Warn(string message, params object?[] args) { }
        public virtual void Warn(Exception exception, string message, params object?[] args) { }
        public virtual void Error(string message, params object?[] args) { }
        public virtual void Error(Exception exception, string message, params object?[] args) { }
    }
}


namespace FluentValidation.Results
{
    public class ValidationFailure
    {
        public ValidationFailure(string propertyName, string errorMessage)
        {
            PropertyName = propertyName;
            ErrorMessage = errorMessage;
        }

        public string PropertyName { get; }
        public string ErrorMessage { get; }
    }
}

namespace NzbDrone.Core.Notifications
{
    using FluentValidation.Results;
    using NzbDrone.Core.MediaFiles.Events;
    using NzbDrone.Core.ThingiProvider;

    /// <summary>
    /// Minimal notification base used when the Lidarr submodule is not present in this worktree.
    /// </summary>
    public abstract class NotificationBase<TSettings>
        where TSettings : IProviderConfig, new()
    {
        protected NotificationBase()
        {
            Settings = new TSettings();
        }

        public TSettings Settings { get; set; }
        public abstract string Name { get; }
        public virtual string Link => string.Empty;
        public virtual ValidationFailure? Test() => null;
        public virtual void OnReleaseImport(AlbumDownloadMessage message) { }
    }
}

namespace NzbDrone.Core.MediaFiles.Events
{
    using NzbDrone.Core.Music;

    public class AlbumDownloadMessage
    {
        public Artist? Artist { get; set; }
        public Album? Album { get; set; }
    }
}

namespace NzbDrone.Core.Messaging.Commands
{
    /// <summary>
    /// Minimal command base class required by Lidarr's command system.
    /// Real Lidarr commands track progress, support cancellation, and emit completion events.
    /// This shim provides just enough for compilation and testing without the full runtime.
    /// </summary>
    public abstract class Command
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime? StartedOn { get; set; }
        public DateTime? EndedOn { get; set; }
        public TimeSpan? Duration => EndedOn.HasValue && StartedOn.HasValue
            ? EndedOn.Value - StartedOn.Value
            : null;
        public string Message { get; set; } = string.Empty;
        public CompletionStatus Status { get; set; } = CompletionStatus.Pending;

        protected Command()
        {
        }

        protected Command(string name)
        {
            Name = name;
        }
    }

    public enum CompletionStatus
    {
        Pending,
        Started,
        Complete,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Command executor interface required by Lidarr's command system.
    /// Implementations are resolved via dependency injection when a command is executed.
    /// </summary>
    public interface IExecute<TCommand>
        where TCommand : Command
    {
        void Execute(TCommand message);
    }
}
