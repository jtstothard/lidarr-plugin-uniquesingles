using System;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Plugins.Scheduling
{
    /// <summary>
    /// Marker interface for metadata providers that also provide a scheduled task.
    /// Implementations expose the command type to run, the interval in minutes,
    /// and the scheduling priority. The ScheduledTaskService listens for provider
    /// lifecycle events and inserts/updates/removes ScheduledTask rows accordingly.
    /// </summary>
    public interface IProvideScheduledTask
    {
        /// <summary>
        /// The command Type that the Scheduler will instantiate and push to the command queue.
        /// The Type's FullName is stored as the ScheduledTask TypeName.
        /// </summary>
        Type CommandType { get; }

        /// <summary>
        /// Interval between automatic executions, in minutes.
        /// </summary>
        int IntervalMinutes { get; }

        /// <summary>
        /// Scheduling priority passed to the command queue when the task fires.
        /// </summary>
        CommandPriority Priority { get; }
    }
}
