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
