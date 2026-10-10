using System;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Plugins.Scheduling
{
    /// <summary>
    /// Bootstrapper that calls <see cref="ScheduledTaskService.InitializeTasks"/>
    /// once Lidarr has finished starting up. This ensures IProvideScheduledTask
    /// providers get their ScheduledTask rows created before the Scheduler
    /// begins polling for pending tasks.
    ///
    /// Lidarr's TaskManager also handles ApplicationStartedEvent and deletes every
    /// ScheduledTask row that is not one of its built-in tasks. To keep the plugin's
    /// schedule across restarts, existing rows are captured on ApplicationStartingEvent
    /// (before TaskManager runs) and tasks are registered after TaskManager has finished.
    /// </summary>
    public class ScheduledTaskServiceStarter : IHandle<ApplicationStartingEvent>, IHandle<ApplicationStartedEvent>
    {
        private readonly ScheduledTaskService _scheduledTaskService;
        private readonly Logger _logger;

        public ScheduledTaskServiceStarter(ScheduledTaskService scheduledTaskService, Logger logger)
        {
            _scheduledTaskService = scheduledTaskService ?? throw new ArgumentNullException(nameof(scheduledTaskService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Handle(ApplicationStartingEvent message)
        {
            _logger.Debug("ApplicationStarting: capturing plugin scheduled task state");
            _scheduledTaskService.CaptureExistingTasks();
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Debug("ApplicationStarted: initializing plugin scheduled tasks");
            _scheduledTaskService.InitializeTasks();
        }
    }
}
