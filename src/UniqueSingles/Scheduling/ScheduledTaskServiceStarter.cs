using System;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Plugins.Scheduling
{
    /// <summary>
    /// Bootstrapper that calls <see cref="ScheduledTaskService.InitializeTasks"/>
    /// once Lidarr has finished starting up. This ensures IProvideScheduledTask
    /// providers get their ScheduledTask rows created before the Scheduler
    /// begins polling for pending tasks.
    /// </summary>
    public class ScheduledTaskServiceStarter : IHandle<ApplicationStartedEvent>
    {
        private readonly ScheduledTaskService _scheduledTaskService;
        private readonly Logger _logger;

        public ScheduledTaskServiceStarter(ScheduledTaskService scheduledTaskService, Logger logger)
        {
            _scheduledTaskService = scheduledTaskService ?? throw new ArgumentNullException(nameof(scheduledTaskService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Debug("ApplicationStarted: initializing plugin scheduled tasks");
            _scheduledTaskService.InitializeTasks();
        }
    }
}
