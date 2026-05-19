# UI Visibility Limitation

## Context

The Unique Singles plugin provides a full-library scan command (`UniqueSinglesScanCommand`) that can be triggered via Lidarr's command system. The implementation is complete and tested, including:

- Command contract with `IExecute<UniqueSinglesScanCommand>` handler
- Per-artist failure isolation
- Idempotent scan behavior
- Comprehensive logging with scan statistics

## Limitation

**Live UAT Required for UI Visibility Verification**

Due to compatibility shim limitations (the worktree lacks the full Lidarr runtime), the command's visibility in the actual Lidarr UI Tasks menu cannot be verified via automated tests in this worktree.

### What IS Verified (Automated)

1. **Command Contract**: The command class `UniqueSinglesScanCommand` extends `Command` with proper naming
2. **Executor Registration**: `UniqueSinglesScanCommandService` implements `IExecute<UniqueSinglesScanCommand>`
3. **Assembly Structure**: Reflection tests confirm the required types are present in the built assembly
4. **Behavioral Correctness**: All scan logic, failure handling, and logging is exercised via in-memory fakes

### What Requires Live UAT

1. **Lidarr UI Discovery**: Whether the command appears in Tasks → Scan for redundant singles
2. **Dependency Injection**: Whether Lidarr correctly resolves the `IExecute<UniqueSinglesScanCommand>` implementation at runtime
3. **Command Triggers**: Whether the UI button/menu item correctly instantiates and executes the command

### Expected Live Behavior

When the plugin is installed in a real Lidarr instance:

1. The command should appear in the UI under a section like "Scheduled Tasks" or "Commands"
2. Clicking the command should trigger `UniqueSinglesScanCommandService.Execute()`
3. Scan progress and results should be visible in the Lidarr UI task history
4. Log messages should appear in the Lidarr log files at the configured log level

### Testing Recommendation

Before production deployment:

1. Install the plugin in a Lidarr development/staging instance
2. Navigate to UI → Tasks (or similar command trigger location)
3. Trigger the full-library scan command
4. Verify that:
   - The command appears in the UI
   - Scan progress is visible
   - Log messages appear in the Lidarr logs
   - Statistics are displayed upon completion

### Why This Limitation Exists

The worktree approach uses compatibility shims (`LidarrTypes.cs`) to compile against Lidarr interfaces without the full Lidarr runtime. This enables fast iteration and testing, but cannot verify runtime behaviors that depend on:

- Lidarr's dependency injection container
- Lidarr's UI routing and discovery mechanisms
- Lidarr's task scheduling infrastructure

This is an expected trade-off in the development workflow and does not indicate a defect in the implementation.

---

**Status**: Implementation complete, awaiting live UAT for final UI verification.
**Risk**: Low - the command contract is correct and behavior is thoroughly tested.