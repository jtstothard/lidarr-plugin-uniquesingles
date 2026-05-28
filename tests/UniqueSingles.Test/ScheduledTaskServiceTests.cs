using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using NzbDrone.Core.Plugins.Scheduling;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Events;
using Xunit;

namespace UniqueSingles.Test;

public class ScheduledTaskServiceTests
{
    // ── ScheduledTaskService event filtering ──────────────────────────

    [Fact]
    public void Handle_ProviderAdded_NonScheduledTaskProvider_DoesNotInsertTask()
    {
        var nonScheduledProvider = new StubMetadataProvider();
        var factory = new StubMetadataFactory(nonScheduledProvider);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        var definition = new MetadataDefinition { Id = 1 };
        var evt = new ProviderAddedEvent<IMetadata>(definition);

        service.Handle(evt);

        Assert.Empty(repo.InsertedTasks);
    }

    [Fact]
    public void Handle_ProviderAdded_ScheduledTaskProvider_InsertsTask()
    {
        var provider = new StubScheduledTaskProvider(
            typeof(UniqueSinglesScanCommand), 120, CommandPriority.Low);
        var factory = new StubMetadataFactory(provider);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        var definition = new MetadataDefinition { Id = 1 };
        var evt = new ProviderAddedEvent<IMetadata>(definition);

        service.Handle(evt);

        Assert.Single(repo.InsertedTasks);
        Assert.Equal(typeof(UniqueSinglesScanCommand).FullName, repo.InsertedTasks[0].TypeName);
        Assert.Equal(120, repo.InsertedTasks[0].Interval);
        Assert.Equal(CommandPriority.Low, repo.InsertedTasks[0].Priority);
        Assert.Equal(repo.InsertedTasks[0].LastExecution, repo.InsertedTasks[0].LastStartTime);
        Assert.True(DateTime.UtcNow - repo.InsertedTasks[0].LastExecution >= TimeSpan.FromMinutes(120));
    }

    [Fact]
    public void Handle_ProviderAdded_ScheduledTaskProvider_DoesNotDuplicateExistingTask()
    {
        var provider = new StubScheduledTaskProvider(
            typeof(UniqueSinglesScanCommand), 120, CommandPriority.Low);
        var factory = new StubMetadataFactory(provider);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        // Pre-insert a task with the same type name
        var existing = new ScheduledTask
        {
            Id = 99,
            TypeName = typeof(UniqueSinglesScanCommand).FullName!,
            Interval = 60,
            Priority = CommandPriority.Low
        };
        repo.AllTasks.Add(existing);

        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        var definition = new MetadataDefinition { Id = 1 };
        service.Handle(new ProviderAddedEvent<IMetadata>(definition));

        // Should update, not insert
        Assert.Empty(repo.InsertedTasks);
        Assert.Equal(120, existing.Interval); // interval updated
    }

    [Fact]
    public void Handle_ProviderUpdated_ScheduledTaskProvider_UpdatesTask()
    {
        var provider = new StubScheduledTaskProvider(
            typeof(UniqueSinglesScanCommand), 240, CommandPriority.Normal);
        var factory = new StubMetadataFactory(provider);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        var existing = new ScheduledTask
        {
            Id = 10,
            TypeName = typeof(UniqueSinglesScanCommand).FullName!,
            Interval = 120,
            Priority = CommandPriority.Low,
            LastExecution = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            LastStartTime = default
        };
        repo.AllTasks.Add(existing);

        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        var definition = new MetadataDefinition { Id = 1 };
        service.Handle(new ProviderUpdatedEvent<IMetadata>(definition));

        Assert.Empty(repo.InsertedTasks);
        Assert.Equal(240, existing.Interval);
        Assert.Equal(CommandPriority.Normal, existing.Priority);
        Assert.Equal(existing.LastExecution, existing.LastStartTime);
    }

    [Fact]
    public void Handle_ProviderUpdated_NonScheduledTaskProvider_DoesNothing()
    {
        var nonScheduledProvider = new StubMetadataProvider();
        var factory = new StubMetadataFactory(nonScheduledProvider);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        var definition = new MetadataDefinition { Id = 1 };
        service.Handle(new ProviderUpdatedEvent<IMetadata>(definition));

        Assert.Empty(repo.InsertedTasks);
        Assert.Empty(repo.UpdatedTasks);
    }

    [Fact]
    public void Handle_ProviderDeleted_CleansUpOrphanedTasks()
    {
        var provider = new StubScheduledTaskProvider(
            typeof(UniqueSinglesScanCommand), 120, CommandPriority.Low);
        var factory = new StubMetadataFactory(provider);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        // Register a task first
        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        var definition = new MetadataDefinition { Id = 1 };
        service.Handle(new ProviderAddedEvent<IMetadata>(definition));
        Assert.Single(repo.InsertedTasks);

        // Now switch factory to return no scheduled providers (simulates deletion)
        factory.Providers = new List<IMetadata>();

        service.Handle(new ProviderDeletedEvent<IMetadata>(1));

        Assert.Single(repo.DeletedTasks);
        Assert.Equal(typeof(UniqueSinglesScanCommand).FullName, repo.DeletedTasks[0].TypeName);
    }

    // ── InitializeTasks ───────────────────────────────────────────────

    [Fact]
    public void InitializeTasks_RegistersAllScheduledProviders()
    {
        var provider = new StubScheduledTaskProvider(
            typeof(UniqueSinglesScanCommand), 1440, CommandPriority.Low);
        var factory = new StubMetadataFactory(provider);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        service.InitializeTasks();

        Assert.Single(repo.InsertedTasks);
        Assert.Equal(1440, repo.InsertedTasks[0].Interval);
    }

    [Fact]
    public void InitializeTasks_IgnoresNonScheduledProviders()
    {
        var nonScheduled = new StubMetadataProvider();
        var factory = new StubMetadataFactory(nonScheduled);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        service.InitializeTasks();

        Assert.Empty(repo.InsertedTasks);
    }

    [Fact]
    public void InitializeTasks_ExceptionInProvider_ContinuesWithOthers()
    {
        var failingProvider = new ThrowingScheduledTaskProvider();
        var goodProvider = new StubScheduledTaskProvider(
            typeof(UniqueSinglesScanCommand), 120, CommandPriority.Low);
        var factory = new StubMetadataFactory(failingProvider, goodProvider);
        var repo = new StubScheduledTaskRepository();
        var cache = new CacheManager();
        using var logger = new TestLogger();

        var service = new ScheduledTaskService(factory, repo, cache, logger.Logger);
        service.InitializeTasks();

        // Good provider should still be registered despite failing one
        Assert.Single(repo.InsertedTasks);
        Assert.True(logger.HasMessageContaining("Failed to register scheduled task"));
    }

    // ── Stub implementations ──────────────────────────────────────────

    private sealed class StubScheduledTaskProvider : IMetadata, IProvideScheduledTask
    {
        private readonly Type _commandType;
        private readonly int _intervalMinutes;
        private readonly CommandPriority _priority;

        public StubScheduledTaskProvider(Type commandType, int intervalMinutes, CommandPriority priority)
        {
            _commandType = commandType;
            _intervalMinutes = intervalMinutes;
            _priority = priority;
        }

        public Type CommandType => _commandType;
        public int IntervalMinutes => _intervalMinutes;
        public CommandPriority Priority => _priority;

        // IMetadata no-ops
        public string Name => "StubScheduled";
        public Type ConfigContract => typeof(UniqueSinglesSettings);
        public ProviderMessage Message => null!;
        public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
        public ProviderDefinition Definition { get; set; } = new MetadataDefinition();
        public FluentValidation.Results.ValidationResult Test() => new();
        public object RequestAction(string stage, IDictionary<string, string> query) => new();
        public string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile) => "";
        public string GetFilenameAfterMove(Artist artist, string albumPath, MetadataFile metadataFile) => "";
        public MetadataFile FindMetadataFile(Artist artist, string path) => null!;
        public MetadataFileResult ArtistMetadata(Artist artist) => null!;
        public MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null!;
        public MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null!;
        public List<ImageFileResult> ArtistImages(Artist artist) => new();
        public List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();
        public List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
    }

    private sealed class ThrowingScheduledTaskProvider : IMetadata, IProvideScheduledTask
    {
        public Type CommandType => typeof(UniqueSinglesScanCommand);
        public int IntervalMinutes => throw new InvalidOperationException("Provider is broken");
        public CommandPriority Priority => CommandPriority.Low;

        public string Name => "ThrowingScheduled";
        public Type ConfigContract => typeof(UniqueSinglesSettings);
        public ProviderMessage Message => null!;
        public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
        public ProviderDefinition Definition { get; set; } = new MetadataDefinition();
        public FluentValidation.Results.ValidationResult Test() => new();
        public object RequestAction(string stage, IDictionary<string, string> query) => new();
        public string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile) => "";
        public string GetFilenameAfterMove(Artist artist, string albumPath, MetadataFile metadataFile) => "";
        public MetadataFile FindMetadataFile(Artist artist, string path) => null!;
        public MetadataFileResult ArtistMetadata(Artist artist) => null!;
        public MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null!;
        public MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null!;
        public List<ImageFileResult> ArtistImages(Artist artist) => new();
        public List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();
        public List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
    }

    private sealed class StubMetadataProvider : IMetadata
    {
        public string Name => "StubNonScheduled";
        public Type ConfigContract => typeof(UniqueSinglesSettings);
        public ProviderMessage Message => null!;
        public IEnumerable<ProviderDefinition> DefaultDefinitions => Enumerable.Empty<ProviderDefinition>();
        public ProviderDefinition Definition { get; set; } = new MetadataDefinition();
        public FluentValidation.Results.ValidationResult Test() => new();
        public object RequestAction(string stage, IDictionary<string, string> query) => new();
        public string GetFilenameAfterMove(Artist artist, TrackFile trackFile, MetadataFile metadataFile) => "";
        public string GetFilenameAfterMove(Artist artist, string albumPath, MetadataFile metadataFile) => "";
        public MetadataFile FindMetadataFile(Artist artist, string path) => null!;
        public MetadataFileResult ArtistMetadata(Artist artist) => null!;
        public MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null!;
        public MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null!;
        public List<ImageFileResult> ArtistImages(Artist artist) => new();
        public List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => new();
        public List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => new();
    }

    private sealed class StubMetadataFactory : IMetadataFactory
    {
        public List<IMetadata> Providers;

        public StubMetadataFactory(params IMetadata[] providers)
        {
            Providers = providers.ToList();
        }

        public List<IMetadata> GetAvailableProviders() => Providers;
        public IMetadata GetInstance(MetadataDefinition definition) => Providers.FirstOrDefault()!;
        public List<MetadataDefinition> All() => new();
        public bool Exists(int id) => false;
        public MetadataDefinition Find(int id) => null!;
        public MetadataDefinition Get(int id) => null!;
        public IEnumerable<MetadataDefinition> Get(IEnumerable<int> ids) => Enumerable.Empty<MetadataDefinition>();
        public MetadataDefinition Create(MetadataDefinition definition) => definition;
        public void Update(MetadataDefinition definition) { }
        public IEnumerable<MetadataDefinition> Update(IEnumerable<MetadataDefinition> definitions) => definitions;
        public void Delete(int id) { }
        public void Delete(IEnumerable<int> ids) { }
        public IEnumerable<MetadataDefinition> GetDefaultDefinitions() => Enumerable.Empty<MetadataDefinition>();
        public IEnumerable<MetadataDefinition> GetPresetDefinitions(MetadataDefinition providerDefinition) => Enumerable.Empty<MetadataDefinition>();
        public void SetProviderCharacteristics(MetadataDefinition definition) { }
        public void SetProviderCharacteristics(IMetadata provider, MetadataDefinition definition) { }
        public FluentValidation.Results.ValidationResult Test(MetadataDefinition definition) => new();
        public object RequestAction(MetadataDefinition definition, string action, IDictionary<string, string> query) => new();
        public List<MetadataDefinition> AllForTag(int tagId) => new();
        public List<IMetadata> Enabled() => Providers;
    }

    private sealed class StubScheduledTaskRepository : IScheduledTaskRepository
    {
        public List<ScheduledTask> AllTasks { get; } = new();
        public List<ScheduledTask> InsertedTasks { get; } = new();
        public List<ScheduledTask> UpdatedTasks { get; } = new();
        public List<ScheduledTask> DeletedTasks { get; } = new();

        public IEnumerable<ScheduledTask> All() => AllTasks;

        public ScheduledTask Insert(ScheduledTask model)
        {
            model.Id = AllTasks.Count + 100;
            AllTasks.Add(model);
            InsertedTasks.Add(model);
            return model;
        }

        public ScheduledTask Update(ScheduledTask model)
        {
            UpdatedTasks.Add(model);
            return model;
        }

        public void Delete(ScheduledTask model)
        {
            AllTasks.RemoveAll(t => t.Id == model.Id);
            DeletedTasks.Add(model);
        }

        public ScheduledTask GetDefinition(Type type) => AllTasks.FirstOrDefault(t => t.TypeName == type.FullName)!;

        // Unused members
        public int Count() => AllTasks.Count;
        public ScheduledTask Find(int id) => AllTasks.FirstOrDefault(t => t.Id == id)!;
        public ScheduledTask Get(int id) => AllTasks.First(t => t.Id == id);
        public ScheduledTask Upsert(ScheduledTask model) => model;
        public void SetFields(ScheduledTask model, params System.Linq.Expressions.Expression<Func<ScheduledTask, object>>[] properties) { }
        public void Delete(int id) { var t = Find(id); if (t != null) Delete(t); }
        public IEnumerable<ScheduledTask> Get(IEnumerable<int> ids) => AllTasks.Where(t => ids.Contains(t.Id));
        public void InsertMany(IList<ScheduledTask> models) { foreach (var m in models) Insert(m); }
        public void UpdateMany(IList<ScheduledTask> models) { }
        public void SetFields(IList<ScheduledTask> models, params System.Linq.Expressions.Expression<Func<ScheduledTask, object>>[] properties) { }
        public void DeleteMany(List<ScheduledTask> models) { }
        public void DeleteMany(IEnumerable<int> ids) { }
        public void Purge(bool vacuum = false) { }
        public bool HasItems() => AllTasks.Count > 0;
        public void SetLastExecutionTime(int id, DateTime executionTime, DateTime startTime) { }
        public ScheduledTask Single() => AllTasks.Single();
        public ScheduledTask SingleOrDefault() => AllTasks.SingleOrDefault();
        public PagingSpec<ScheduledTask> GetPaged(PagingSpec<ScheduledTask> pagingSpec) => pagingSpec;
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

        public List<string> AllMessages
        {
            get
            {
                _factory.Flush();
                return _target.Logs.ToList();
            }
        }

        public void Dispose()
        {
            _factory.Flush();
            _factory.Dispose();
        }
    }
}
