using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
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

        public UniqueSinglesScanTask(
            ISingleCleanupService cleanupService,
            IArtistService artistService,
            Logger logger)
        {
            _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
            _artistService = artistService ?? throw new ArgumentNullException(nameof(artistService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override string Name => "UniqueSingles Scan";

        public override Type CommandType => typeof(UniqueSinglesScanCommand);

        public override int IntervalMinutes => Settings?.ScanIntervalMinutes ?? 1440;

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

            var options = Settings?.ToCleanupOptions() ?? new SingleCleanupOptions(
                3000,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" },
                Tier3Action.FlagOnly);

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
                "Scanned {0} artists: {1} cleaned, {2} skipped, {3} need review{4}",
                artistsScanned,
                aggregatedResult.Cleaned,
                aggregatedResult.Skipped,
                aggregatedResult.ReviewNeeded,
                artistsFailed > 0 ? $", {artistsFailed} failed" : "");
        }
    }
}
