using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins.Scheduling;

namespace NzbDrone.Core.Plugins
{
    /// <summary>
    /// Metadata-provider-backed scheduled task for full-library redundant single scanning.
    /// Inherits ScheduledTaskBase (which inherits MetadataBase) to appear in Settings > Metadata
    /// while providing a scheduled task visible in Settings > Tasks.
    ///
    /// This is the sole IExecute&lt;UniqueSinglesScanCommand&gt; implementation — all scan
    /// execution is routed through here, delegating to ISingleCleanupService.ScanArtistWithOptions
    /// for the actual cleanup work per artist.
    /// </summary>
    public class UniqueSinglesScanTask : ScheduledTaskBase<UniqueSinglesSettings>, IExecute<UniqueSinglesScanCommand>
    {
        private readonly ISingleCleanupService _cleanupService;
        private readonly IArtistService _artistService;
        private readonly Logger _logger;
        private readonly Lazy<INotificationFactory> _notificationFactory;

        /// <summary>
        /// INotificationFactory is resolved lazily to avoid a DryIoc recursive dependency at startup.
        /// Eager injection triggers resolution of all INotification implementations (including
        /// third-party plugins like Tubifarry's QueueCleaner), which can have circular dependency
        /// chains. Lazy resolution defers this until scan execution time, when the container
        /// is fully initialized and the dependency graph is stable.
        /// </summary>
        public UniqueSinglesScanTask(
            ISingleCleanupService cleanupService,
            IArtistService artistService,
            Lazy<INotificationFactory> notificationFactory,
            Logger logger)
        {
            _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
            _artistService = artistService ?? throw new ArgumentNullException(nameof(artistService));
            _notificationFactory = notificationFactory ?? throw new ArgumentNullException(nameof(notificationFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Safely resolves the scan interval from metadata provider settings.
        /// Handles null Definition and type conversion failures, logging fallback to default.
        /// Matches the error handling pattern of ResolveNotificationSettings.
        /// </summary>
        private int TryGetMetadataSettings()
        {
            try
            {
                if (Definition == null)
                {
                    _logger.Debug(
                        "UniqueSingles interval: metadata provider Definition is null, using default 1440 minutes");
                    return 1440;
                }

                if (Definition.Settings is UniqueSinglesSettings settings)
                {
                    var interval = settings.ScanIntervalMinutes;
                    _logger.Debug(
                        "UniqueSingles interval: using metadata provider settings, interval {0} minutes", interval);
                    return interval;
                }

                _logger.Debug(
                    "UniqueSingles interval: metadata provider Settings is not UniqueSinglesSettings type, using default 1440 minutes");
                return 1440;
            }
            catch (NullReferenceException ex)
            {
                _logger.Warn(ex,
                    "UniqueSingles interval: NullReferenceException when accessing metadata provider settings, using default 1440 minutes");
                return 1440;
            }
            catch (InvalidCastException ex)
            {
                _logger.Warn(ex,
                    "UniqueSingles interval: InvalidCastException when casting metadata provider settings, using default 1440 minutes");
                return 1440;
            }
        }

        /// <summary>
        /// Resolves cleanup options using notification settings (Settings > Connect) as the
        /// source of truth. Falls back to metadata provider settings, then to hardcoded defaults.
        /// </summary>
        private SingleCleanupOptions ResolveCleanupOptions()
        {
            try
            {
                var allNotifications = _notificationFactory.Value.All();
                var matching = allNotifications
                    .Where(d => d.Settings is UniqueSinglesSettings)
                    .ToList();

                if (matching.Count > 0)
                {
                    if (matching.Count > 1)
                    {
                        _logger.Warn(
                            "UniqueSingles scan: multiple UniqueSingles notifications found ({0}), using first",
                            matching.Count);
                    }

                    var settings = (UniqueSinglesSettings)matching[0].Settings;
                    _logger.Info("UniqueSingles scan: using notification settings as source of truth");
                    return settings.ToCleanupOptions();
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "UniqueSingles scan: failed to resolve notification settings, falling back");
            }

            if (Settings != null)
            {
                _logger.Info("UniqueSingles scan: using metadata provider settings (no notification found)");
                return Settings.ToCleanupOptions();
            }

            _logger.Info("UniqueSingles scan: using default settings (no notification or metadata provider settings)");
            return new SingleCleanupOptions(
                3000,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" },
                Tier3Action.FlagOnly);
        }

        public override string Name => "UniqueSingles Scan";

        public override Type CommandType => typeof(UniqueSinglesScanCommand);

        /// <summary>
        /// Resolves the scan interval from metadata provider settings with proper error handling.
        /// Falls back to 1440-minute default on any failure and logs the fallback.
        /// </summary>
        public override int IntervalMinutes => TryGetMetadataSettings();

        public override CommandPriority Priority => CommandPriority.Low;

        /// <summary>
        /// Executes the full-library scan. Iterates all monitored artists,
        /// delegates per-artist scanning to ISingleCleanupService.ScanArtistWithOptions,
        /// aggregates results, logs a structured summary, and sets the completion message
        /// on the command for toast display in the Lidarr UI.
        /// </summary>
        public void Execute(UniqueSinglesScanCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            // Per-artist scan: if ArtistId is provided, scan only that artist
            if (command.ArtistId.HasValue && command.ArtistId.Value > 0)
            {
                ExecutePerArtistScan(command);
                return;
            }

            _logger.Info("UniqueSingles scheduled scan starting");

            List<Artist> allArtists;
            try
            {
                allArtists = _artistService.GetAllArtists() ?? new List<Artist>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "UniqueSingles scan failed: could not load artists");
                throw;
            }

            var monitoredArtists = allArtists.Where(a => a.Monitored).ToList();
            var artistsScanned = 0;
            var artistsFailed = 0;
            var aggregatedResult = CleanupResult.Empty;

            _logger.Info(
                "UniqueSingles scan: {0} total artists, {1} monitored, {2} skipped (unmonitored)",
                allArtists.Count,
                monitoredArtists.Count,
                allArtists.Count - monitoredArtists.Count);

            var options = ResolveCleanupOptions();

            foreach (var artist in monitoredArtists)
            {
                try
                {
                    var result = _cleanupService.ScanArtistWithOptions(artist, options);
                    aggregatedResult += result;
                    artistsScanned++;

                    _logger.Debug(
                        "UniqueSingles scan: artist complete. artistId={0} artist='{1}' candidatesChecked={2} cleaned={3} skipped={4} reviewNeeded={5}",
                        artist.Id,
                        artist.Name,
                        result.CandidatesChecked,
                        result.Cleaned,
                        result.Skipped,
                        result.ReviewNeeded);
                }
                catch (Exception ex)
                {
                    artistsFailed++;
                    _logger.Warn(
                        ex,
                        "UniqueSingles scan: artist scan failed (continuing with next artist). artistId={0} artist='{1}'",
                        artist.Id,
                        artist.Name);
                }
            }

            _logger.Info(
                "UniqueSingles scan complete: artistsScanned={0} artistsSkipped={1} artistsFailed={2} candidatesChecked={3} cleaned={4} skipped={5} reviewNeeded={6} unmonitorFailures={7} deleteFailures={8}",
                artistsScanned,
                allArtists.Count - monitoredArtists.Count,
                artistsFailed,
                aggregatedResult.CandidatesChecked,
                aggregatedResult.Cleaned,
                aggregatedResult.Skipped,
                aggregatedResult.ReviewNeeded,
                aggregatedResult.UnmonitorFailures,
                aggregatedResult.DeleteFailures);

            command.ResultMessage = string.Format(
                "{0} artists scanned, {1} singles cleaned, {2} skipped, {3} flagged for review{4}",
                artistsScanned,
                aggregatedResult.Cleaned,
                aggregatedResult.Skipped,
                aggregatedResult.ReviewNeeded,
                artistsFailed > 0 ? $", {artistsFailed} failed" : "");
        }

        /// <summary>
        /// Scans a single artist by ID. Resolves cleanup options, fetches the artist,
        /// runs the scan, and sets a completion message on the command.
        /// Logs error and returns early if the artist is not found.
        /// </summary>
        private void ExecutePerArtistScan(UniqueSinglesScanCommand command)
        {
            var artistId = command.ArtistId.GetValueOrDefault();
            _logger.Info("UniqueSingles per-artist scan starting: artistId={0}", artistId);

            var options = ResolveCleanupOptions();

            Artist artist;
            try
            {
                artist = _artistService.GetArtist(artistId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "UniqueSingles per-artist scan failed: artist {0} not found", artistId);
                command.ResultMessage = string.Format("Per-artist scan failed: artist {0} not found", artistId);
                return;
            }

            if (artist == null)
            {
                _logger.Error("UniqueSingles per-artist scan failed: artist {0} not found", artistId);
                command.ResultMessage = string.Format("Per-artist scan failed: artist {0} not found", artistId);
                return;
            }

            var result = _cleanupService.ScanArtistWithOptions(artist, options);

            var failedSuffix = result.UnmonitorFailures > 0 || result.DeleteFailures > 0
                ? string.Format(", {0} unmonitor failures, {1} delete failures", result.UnmonitorFailures, result.DeleteFailures)
                : "";

            command.ResultMessage = string.Format(
                "1 artist scanned, {0} singles cleaned, {1} skipped, {2} flagged for review{3}",
                result.Cleaned,
                result.Skipped,
                result.ReviewNeeded,
                failedSuffix);

            _logger.Info(
                "UniqueSingles per-artist scan complete: artistId={0} artist='{1}' candidatesChecked={2} cleaned={3} skipped={4} reviewNeeded={5} unmonitorFailures={6} deleteFailures={7}",
                artist.Id,
                artist.Name,
                result.CandidatesChecked,
                result.Cleaned,
                result.Skipped,
                result.ReviewNeeded,
                result.UnmonitorFailures,
                result.DeleteFailures);
        }
    }
}
