using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Plugins.Scheduling
{
    /// <summary>
    /// Keeps the ScheduledTask table in sync with metadata providers that implement
    /// IProvideScheduledTask. Handles provider add/update/delete events to insert,
    /// update, or remove ScheduledTask rows and invalidate the TaskManager cache.
    /// Called at startup via <see cref="ScheduledTaskServiceStarter"/> to register
    /// tasks for all available providers.
    /// </summary>
    public class ScheduledTaskService :
        IHandle<ProviderAddedEvent<IMetadata>>,
        IHandle<ProviderUpdatedEvent<IMetadata>>,
        IHandle<ProviderDeletedEvent<IMetadata>>
    {
        private readonly IMetadataFactory _metadataFactory;
        private readonly IScheduledTaskRepository _scheduledTaskRepository;
        private readonly ICached<ScheduledTask> _cache;
        private readonly Logger _logger;

        /// <summary>
        /// Tracks command type names that were registered by this service,
        /// enabling orphan detection on provider deletion.
        /// </summary>
        private readonly HashSet<string> _registeredCommandTypes = new();

        public ScheduledTaskService(
            IMetadataFactory metadataFactory,
            IScheduledTaskRepository scheduledTaskRepository,
            ICacheManager cacheManager,
            Logger logger)
        {
            _metadataFactory = metadataFactory ?? throw new ArgumentNullException(nameof(metadataFactory));
            _scheduledTaskRepository = scheduledTaskRepository ?? throw new ArgumentNullException(nameof(scheduledTaskRepository));
            _cache = cacheManager.GetCache<ScheduledTask>(typeof(TaskManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Handle(ProviderAddedEvent<IMetadata> message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            var provider = _metadataFactory.GetInstance((MetadataDefinition)message.Definition);
            if (provider is IProvideScheduledTask scheduledTaskProvider)
            {
                _logger.Debug("ProviderAdded: registering scheduled task for {0}", scheduledTaskProvider.CommandType.Name);
                RegisterTask(scheduledTaskProvider);
            }
        }

        public void Handle(ProviderUpdatedEvent<IMetadata> message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            var provider = _metadataFactory.GetInstance((MetadataDefinition)message.Definition);
            if (provider is IProvideScheduledTask scheduledTaskProvider)
            {
                _logger.Debug("ProviderUpdated: updating scheduled task for {0}", scheduledTaskProvider.CommandType.Name);
                UpdateTask(scheduledTaskProvider);
            }
        }

        public void Handle(ProviderDeletedEvent<IMetadata> message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            // Provider is already deleted from DB — re-sync and clean orphaned tasks.
            _logger.Debug("ProviderDeleted (id={0}): cleaning orphaned scheduled tasks", message.ProviderId);
            CleanupOrphanedTasks();
        }

        /// <summary>
        /// Called at startup to ensure all IProvideScheduledTask providers have
        /// ScheduledTask rows. Idempotent — skips rows that already exist.
        /// </summary>
        public void InitializeTasks()
        {
            var providers = _metadataFactory.GetAvailableProviders()
                .OfType<IProvideScheduledTask>()
                .ToList();

            _logger.Debug("Initializing scheduled tasks from {0} IProvideScheduledTask providers", providers.Count);

            foreach (var provider in providers)
            {
                try
                {
                    RegisterTask(provider);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to register scheduled task for {0}", provider.GetType().Name);
                }
            }

            CleanupOrphanedTasks();
        }

        private void RegisterTask(IProvideScheduledTask provider)
        {
            var typeName = provider.CommandType.FullName!;
            var existingTask = FindScheduledTask(typeName);

            if (existingTask != null)
            {
                _logger.Debug("Scheduled task already exists for {0}, updating interval/priority", typeName);
                existingTask.Interval = provider.IntervalMinutes;
                existingTask.Priority = provider.Priority;
                _scheduledTaskRepository.Update(existingTask);
                _cache.Set(typeName, existingTask);
                _registeredCommandTypes.Add(typeName);
                return;
            }

            var task = new ScheduledTask
            {
                TypeName = typeName,
                Interval = provider.IntervalMinutes,
                Priority = provider.Priority,
                LastExecution = DateTime.UtcNow
            };

            _scheduledTaskRepository.Insert(task);
            _cache.Set(typeName, task);
            _registeredCommandTypes.Add(typeName);
            _logger.Info("Registered scheduled task: {0} interval={1}min priority={2}", typeName, provider.IntervalMinutes, provider.Priority);
        }

        private void UpdateTask(IProvideScheduledTask provider)
        {
            var typeName = provider.CommandType.FullName!;
            var existingTask = FindScheduledTask(typeName);

            if (existingTask == null)
            {
                _logger.Debug("Scheduled task not found for {0}, creating new", typeName);
                RegisterTask(provider);
                return;
            }

            existingTask.Interval = provider.IntervalMinutes;
            existingTask.Priority = provider.Priority;
            _scheduledTaskRepository.Update(existingTask);
            _cache.Set(typeName, existingTask);
            _registeredCommandTypes.Add(typeName);
            _logger.Info("Updated scheduled task: {0} interval={1}min priority={2}", typeName, provider.IntervalMinutes, provider.Priority);
        }

        /// <summary>
        /// Removes ScheduledTask rows for command types that were previously registered
        /// by this service but no longer have a corresponding IProvideScheduledTask provider.
        /// </summary>
        private void CleanupOrphanedTasks()
        {
            var activeCommandTypes = _metadataFactory.GetAvailableProviders()
                .OfType<IProvideScheduledTask>()
                .Select(p => p.CommandType.FullName!)
                .ToHashSet();

            var orphaned = _registeredCommandTypes
                .Where(registered => !activeCommandTypes.Contains(registered))
                .ToList();

            foreach (var typeName in orphaned)
            {
                var task = FindScheduledTask(typeName);
                if (task != null)
                {
                    _scheduledTaskRepository.Delete(task.Id);
                    _cache.Remove(typeName);
                    _logger.Info("Removed orphaned scheduled task: {0}", typeName);
                }

                _registeredCommandTypes.Remove(typeName);
            }
        }

        private ScheduledTask FindScheduledTask(string typeName)
        {
            try
            {
                return _scheduledTaskRepository.All()
                    .FirstOrDefault(t => t.TypeName == typeName);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to query scheduled tasks for {0}", typeName);
                return null;
            }
        }
    }
}
