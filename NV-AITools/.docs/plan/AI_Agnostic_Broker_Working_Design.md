# NV-AITools AI-Agnostic Broker

*Implemented design for exposing authenticated, read-only NV-AITools operations to sandboxed AI agents through a durable filesystem queue.*

## 1. Status

This document describes the agreed direction and its implementation through version 1.2. The broker, queue client, tray host, packaging, skill contract, bounded parallel diff pipelines, pending filename filters, and repository-local integration tests were implemented by August 19, 2026. Authenticated Plastic concurrency in a real configured workspace and login/install/upgrade/uninstall behavior remain explicit receiving-machine acceptance checks because they require a real Plastic authentication profile or per-user Windows state.

The intended result is:

- One installed `NV-AITools.exe`.
- One tiny `StartBroker.vbs` hidden login launcher.
- One clear `config.ini` beside the executable.
- One background broker process running as the authenticated Windows user and represented by a system-tray icon.
- One AI skill exposing the existing read-only commands.
- One ignored coordination directory inside each configured workspace.
- No Windows service, named-pipe ACL, AI-specific Windows identity, MCP server, or continuous directory polling.

This design supersedes the assumption in [UVCS_Tools_Implementation_Outline.md](../UVCS_Tools_Implementation_Outline.md) that every AI invocation can execute Plastic directly from its own process.

## 2. User Goals

The system should provide:

1. A `config.ini` beside the installed application where the user lists workspaces to watch.
2. A one-time, per-user installation without administrator access.
3. Automatic broker startup under the same Windows user that owns the Plastic authentication profile.
4. A visible system-tray status with direct access to configuration, logs, reload, and shutdown.
5. One skill that AI agents use without knowing how the broker works.
6. The same public NV-AITools command line and stdout, stderr, and exit-code contracts already used by humans and agents.
7. No continuous polling during normal operation.
8. Reliable recovery when filesystem notifications overflow or the broker restarts.
9. A narrow security boundary that cannot execute arbitrary commands supplied by a workspace or AI agent.
10. Multiple AI clients can submit requests concurrently from the same workspace or different configured workspaces without filename collisions, lost requests, cross-workspace execution, or unnecessary global serialization.

## 3. Core Design

`NV-AITools.exe` runs in two roles.

### 3.1 Broker role

The broker is started outside AI sandboxes under the normal authenticated Windows user. It:

- Reads `config.ini` from `AppContext.BaseDirectory`.
- Creates one watcher for each configured workspace.
- Receives requests through that workspace's coordination directory.
- Executes the existing fixed read-only command implementations.
- Runs `cm.exe` and `git.exe` using the user's normal authentication and environment.
- Writes results back to the coordination directory.
- Hosts a minimal system-tray interface for status and lifecycle control.
- Maintains an independent serial execution lane for each configured workspace, with a fixed broker-wide concurrency limit.

Only the broker may reach [ProcessRunner.RunAsync](../../Infrastructure/ProcessRunner.cs#L11).

### 3.2 Client role

Normal command invocations remain the client interface:

```text
NV-AITools.exe status [--workspace <path>]
NV-AITools.exe pending-changes-diffs [--workspace <path>]
    [--file-filter <glob>]...
NV-AITools.exe changeset-diffs --from <changeset> --to <changeset>
    [--workspace <path>]
    [--algorithm histogram|patience|default|minimal]
```

The client:

- Parses and validates the existing command line.
- Walks upward from the supplied path until it finds the broker-created `.nv-ai-tools` coordination directory.
- Never invokes Plastic or reads broker configuration while resolving the workspace client-side.
- Writes a typed request into the workspace coordination directory.
- Waits for the broker to claim and complete it.
- Copies result stdout to its own stdout.
- Copies result stderr to its own stderr.
- Returns the broker's exit code.

The skill and human-facing wrappers therefore retain the same interface. They do not need to understand the queue or broker.

## 4. Configuration

The installed directory contains:

```text
NV-AITools.exe
StartBroker.vbs
config.ini
broker.log
```

The initial configuration format is deliberately small:

```ini
[Workspaces]
Path1=<absolute workspace root>
Path2=<absolute workspace root>
```

Configuration rules:

- Only the `[Workspaces]` section and numbered `Path` keys are supported initially.
- Blank lines and lines beginning with `;` or `#` are ignored.
- Every path must be absolute.
- Paths are canonicalized, trimmed of unnecessary trailing separators, and compared case-insensitively.
- A syntactically valid configuration with no workspace paths is allowed so a first-run broker can display its tray icon and let the user open the configuration.
- Empty path values, duplicate paths, missing directories, malformed keys, or invalid paths reject the candidate configuration with an actionable error.
- Each path must identify a workspace root rather than an arbitrary subdirectory.
- Configuration is loaded at broker startup and when the user selects `Reload Configuration` from the tray menu.
- Reload parses and validates the entire replacement configuration before changing any active watcher.
- A failed reload leaves the last valid configuration and watchers running, writes an actionable log entry, and shows a concise tray notification.
- Upgrades preserve an existing configuration file.

The narrow format does not require a general-purpose INI dependency.

## 5. Workspace Coordination Directory

Each configured workspace contains one runtime directory:

```text
.nv-ai-tools/
  requests/
  processing/
  results/
```

The setup instructions must add `.nv-ai-tools/` to the workspace's source-control ignore configuration. The directory contains only transient coordination data and must never appear as a pending project change.

The broker creates missing runtime directories. Their absence is not a workspace setup failure.

The coordination directory is intentionally writable by the sandboxed client. It is an untrusted request channel, not a trusted executable location.

The broker rejects filesystem reparse points for the workspace, coordination root, and queue subdirectories, and verifies every opened directory against its expected resolved location. It retains non-delete-sharing directory handles for the workspace runtime's lifetime so an untrusted client cannot rename or replace a validated queue path before a broker write or delete. Queue and result publication uses create-new files and atomic moves, so an untrusted client cannot select an existing destination for broker output. The broker never follows caller-supplied paths from request content.

## 6. Request Protocol

### 6.1 Request creation

For each command, the client:

1. Generates 16 cryptographically strong random bytes and represents them as exactly 32 lowercase hexadecimal characters.
2. Creates `requests/<id>.tmp` with create-new semantics so an existing filename is never overwritten.
3. Flushes and closes the file.
4. Atomically renames it to `requests/<id>.request` on the same volume.
5. Waits for the request to disappear from `requests`, indicating that the broker claimed it.

If create-new reports that the candidate filename already exists, the client generates a new random identifier and retries. The broker accepts only filenames matching the exact request-ID format. Request identifiers cannot contain path separators or other filename content.

`RandomNumberGenerator.GetBytes` produces cryptographically strong random bytes, and create-new file creation reports an error instead of overwriting an existing file:

- [`RandomNumberGenerator.GetBytes` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator.getbytes?view=net-10.0)
- [.NET I/O error documentation](https://learn.microsoft.com/en-us/dotnet/standard/io/handling-io-errors)

### 6.2 Why the request ID is random

A process-local 64-bit atomic counter is not a safe request identifier for this architecture. Each AI invocation is a separate client process with its own counter variable. Two clients operating in the same workspace could both produce counter value `1`. Adding the workspace path would distinguish different workspaces but would not distinguish those two clients in the same workspace.

`Interlocked.Increment` atomically increments one specified in-memory variable; it does not create a machine-wide or cross-process counter. See the [`Interlocked.Increment` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked.increment?view=net-10.0).

A truly global counter would require a shared counter file or shared-memory object, cross-process locking, durable persistence, corruption recovery, and rules for reset, reinstall, and overflow. It would introduce a synchronization hotspot before a client could publish any request. Broker-assigned counters do not remove the problem because a client still needs a unique filename before the broker can receive the request.

The random identifier plus create-new file creation is decentralized and collision-safe:

- Clients do not share mutable state.
- Clients can submit simultaneously.
- Restarts and reinstalls cannot reset an identifier sequence.
- A theoretical random collision is detected rather than overwritten.
- The workspace directory already provides the workspace namespace, so embedding a workspace path in the filename adds no uniqueness within that workspace.

Logs correlate a request using the configured workspace root together with the random request ID. Workspace paths are not embedded into queue filenames.

### 6.3 Request contents

A request contains only typed protocol data:

```json
{
  "protocol": 1,
  "id": "0123456789abcdef0123456789abcdef",
  "tool": "status",
  "arguments": {}
}
```

Supported tool identifiers are fixed:

- `status`
- `pending-changes-diffs`
- `changeset-diffs`

The request may contain only the parameters already supported by [CommandLine.Parse](../../Cli/CommandLine.cs#L34):

- Non-negative `from` and `to` changeset numbers.
- A known diff-algorithm enum.
- The retained protocol-version-1 rename-detection boolean, which must be `false`.

The request must never contain:

- An executable or script path.
- A raw command line or argument list.
- A working directory.
- A workspace path.
- An output path.
- Environment-variable changes.
- Shell, PowerShell, Plastic, or Git passthrough text.

The broker associates the request with a workspace based on the watcher that claimed it. `config.ini`, not request content, is the workspace allowlist.

### 6.4 Claiming

The broker claims a request by atomically moving it from `requests` to `processing`.

- A successful move gives one broker worker ownership.
- A failed move means another drain already claimed or removed it.
- The broker reads and validates the claimed file exactly once.
- The broker executes from the parsed in-memory request and never rereads mutable request content.
- An intake drain claims and validates all currently available requests before any long-running Plastic or Git operation begins.
- A claimed request is enqueued into the execution lane belonging to the workspace from which it was claimed.

Each configured workspace has one serial execution lane. Top-level requests for the same workspace execute one at a time, providing a simple ownership model for workspace-specific activity. Version 1.1 diff commands may internally run bounded read-only Plastic operations for different files in parallel. The order is the broker's claim order; callers must not depend on ordering between requests submitted concurrently.

Different workspace lanes may execute concurrently. The first version uses a fixed broker-wide `SemaphoreSlim` capacity of four, allowing up to four workspaces to perform tool operations simultaneously while preventing an unbounded number of Plastic and Git child processes. Additional workspace requests remain durably queued in `processing` until a slot is available. This limit is an internal constant rather than another user-facing configuration setting.

### 6.5 Results

The broker writes:

```text
results/<id>.stdout.tmp
results/<id>.stderr.tmp
results/<id>.result.tmp
```

It then commits the response in this order:

1. Rename stdout to `results/<id>.stdout`.
2. Rename stderr to `results/<id>.stderr`.
3. Rename the completion record to `results/<id>.result` last.

The completion record contains:

```json
{
  "protocol": 1,
  "id": "0123456789abcdef0123456789abcdef",
  "exitCode": 0
}
```

Writing the completion record last guarantees that the client never treats partial output as complete.

After consuming the result, the client deletes its request, processing, output, and completion files where present.

### 6.6 Concurrent-client behavior

Multiple clients in one workspace can publish simultaneously because every client uses an independently generated request ID and create-new file creation. The broker promptly claims every complete request, then the workspace's serial lane executes them safely one at a time.

Clients in different configured workspaces use different coordination directories and different execution lanes. Up to four workspace operations can run at the same time.

Every command invocation must also own an independent temporary directory. No command may use a fixed temporary filename, shared mutable output buffer, process-wide console redirection, or workspace-global current-directory mutation. Result files remain isolated by request ID.

One workspace's queue, watcher error, invalid request, command failure, or long-running operation must not prevent other workspace lanes from accepting and processing requests, subject only to the fixed global concurrency limit.

## 7. Client Timeouts and Failure Behavior

The client uses two wait phases.

### 7.1 Claim timeout

- The client waits up to three seconds for the request to move from `requests` to `processing`.
- If the request remains unclaimed, the client removes it and returns exit code `3`.
- The diagnostic states that the broker is not running or the workspace is not configured.
- If deletion fails because the broker claimed the request concurrently, the client proceeds to the completion wait.

This replaces a separate heartbeat file or broker-discovery protocol.

### 7.2 Completion timeout

- The claim timeout ends once the broker moves the request into `processing`, even if the request is waiting behind another request for that workspace or behind the broker-wide concurrency limit.
- The client completion timeout is fixed at twelve hours, covering queue wait plus legitimate large changeset operations without adding a user-facing setting.
- The broker applies the command-specific execution timeout only after the request acquires its workspace lane and a global execution slot.
- If the broker does not publish a completion record, the client returns an external or internal failure with a clear diagnostic.
- The broker remains responsible for killing timed-out child processes and enforcing output-size limits.

Existing exit codes remain unchanged:

| Exit code | Meaning |
|---|---|
| `0` | Success. |
| `2` | Invalid command input or invalid request. |
| `3` | Broker, dependency, configuration, or workspace failure. |
| `4` | Plastic, Git, or comparison process failure. |
| `5` | Protocol, parsing, temporary-file, or internal failure. |

There is no fallback to direct `cm.exe` execution and no request for the AI process to leave its sandbox.

## 8. Event-Driven Watcher Reliability

The `.request` file is the durable queue item. `FileSystemWatcher` is only a wake-up mechanism.

Microsoft documents that `FileSystemWatcher.Error` occurs when monitoring cannot continue or the internal event buffer overflows. An overflow means individual events can be lost. Microsoft also recommends limiting `NotifyFilter`, `Filter`, and `IncludeSubdirectories` to reduce buffer pressure:

- [FileSystemWatcher documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher)
- [InternalBufferSize documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher.internalbuffersize)
- [InternalBufferOverflowException documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.internalbufferoverflowexception)

### 8.1 Watcher configuration

Each workspace request directory uses:

```text
Filter = "*.request"
IncludeSubdirectories = false
NotifyFilter = NotifyFilters.FileName
```

The broker subscribes to:

- `Created`
- `Renamed`
- `Error`

Both `Created` and `Renamed` are handled because the client publishes a request through an atomic rename.

### 8.2 Startup sequence

For each configured workspace, the broker:

1. Creates the runtime directories.
2. Constructs the watcher.
3. Attaches event handlers.
4. Enables events.
5. Enumerates all existing `.request` files once.
6. Enqueues a drain signal if work exists.

Enabling the watcher before the initial enumeration closes the startup race. A request created during enumeration may produce both an event and a scan result, but atomic claiming makes duplicate drain signals harmless.

### 8.3 Event handling

Watcher callbacks do not perform filesystem enumeration, JSON parsing, or external commands. They only enqueue a coalesced intake-drain signal for their workspace.

The intake worker responds to a signal by enumerating all `.request` files rather than trusting the individual event path. It promptly claims, reads, validates, and enqueues every complete request for that workspace. External command execution happens later on the workspace execution lane. This provides one consistent intake path for normal events, duplicate events, overflow recovery, and simultaneous submissions.

### 8.4 Error recovery

On any watcher `Error`, not only `InternalBufferOverflowException`, the broker:

1. Logs the error.
2. Disposes the affected watcher.
3. Enumerates and drains the durable request directory.
4. Recreates the watcher.
5. Retries watcher creation with bounded backoff only while the directory or volume is unavailable.
6. Enumerates once more after monitoring resumes.

There is no normal-operation polling loop. Enumeration occurs only:

- At broker startup.
- After a watcher notification.
- During explicit watcher-error recovery.
- During broker crash recovery.

### 8.5 Broker crash recovery

At startup, the broker examines both `requests` and `processing`.

- Unclaimed requests are processed normally.
- Interrupted `processing` requests are moved back to `requests` and retried.
- Abandoned completed results older than a fixed retention period are removed during startup cleanup.

The three current tools are read-only, so retrying after an indeterminate broker crash is safe. Mutating tools are outside this protocol's initial scope and would require stronger exactly-once or idempotency contracts.

## 9. Security Model

### 9.1 Trusted components

- The installed `NV-AITools.exe`.
- The adjacent `config.ini`.
- The broker process running as the authenticated user.
- The fixed command implementations compiled into the executable.

The per-user install remains writable by the Windows user. Its trust model still assumes the AI runs in a sandbox that cannot modify the installed application or configuration. An unrestricted AI operating as that user can replace either file and therefore bypass this design.

### 9.2 Untrusted components

- Every request file.
- Every workspace file.
- Any BAT wrapper copied into a workspace.
- All request parameters.
- The client process running inside the AI sandbox.

The broker must never execute a BAT, executable, script, or manifest from a workspace. Workspace BAT files may submit requests for human convenience, but they cannot define broker behavior.

### 9.3 Capability boundary

Write access to a configured workspace's coordination directory grants the ability to invoke the fixed read-only tools for that workspace. It does not grant:

- Arbitrary command execution.
- Access to other configured workspaces.
- Arbitrary Plastic or Git arguments.
- Arbitrary filesystem reads through caller-provided paths.
- Caller-selected output writes.

This makes the transport independent of Codex, Claude, or any other AI-specific process identity. Any sandboxed agent that can run the client and write inside its workspace can use the same skill contract.

## 10. Tray Application and Broker Lifetime

The broker is a per-user system-tray application, not a Windows service. Running in the interactive user session gives it access to the user's Plastic authentication profile and makes its current state visible without adding a full GUI.

### 10.1 Single-executable roles

The existing executable remains a console application and gains two internal broker commands:

- `broker-start` launches the persistent broker and waits for explicit initialization success or failure; the login launcher invokes it with a hidden window.
- `broker-run` hosts the watcher service, request worker, and system-tray interface.

Normal `status`, `pending-changes-diffs`, and `changeset-diffs` commands remain console clients with working stdout, stderr, and exit codes.

The project configuration in [NV-AITools.csproj](../../NV-AITools.csproj#L3) changes to:

```xml
<OutputType>Exe</OutputType>
<TargetFramework>net10.0-windows</TargetFramework>
<UseWindowsForms>true</UseWindowsForms>
```

`OutputType` deliberately remains `Exe`, not `WinExe`, because AI and human client invocations still require a normal console contract. Enabling WinForms requires a Windows-specific target framework and `UseWindowsForms`. `WinExe` is optional according to the [.NET Desktop SDK documentation](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop).

Adding the Windows Desktop runtime will increase the size of the self-contained single-file release. It does not require an additional installed executable or .NET runtime installation.

### 10.2 Tray host

The top-level entry point in [Program.cs](../../Program.cs#L1) becomes an explicit `[STAThread] Main` method. In `broker-run` mode it initializes WinForms and starts a message loop using an `ApplicationContext`:

```text
System.Windows.Forms.Application.Run(BrokerApplicationContext)
```

The application context owns:

- One `NotifyIcon`.
- One context menu.
- The broker host and its cancellation source.
- Graceful application shutdown.

WinForms requires an active message loop for tray interaction even when the application has no visible window. `Application.Run(ApplicationContext)` provides that lifetime model. See the [`Application.Run` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.application.run?view=windowsdesktop-10.0).

The tray host and broker worker remain separate responsibilities. Watchers and external operations run asynchronously and never block the WinForms message thread.

### 10.3 Minimal tray interface

The first version exposes only:

- `NV-AITools - Running` as a disabled status item.
- `Open config.ini`.
- `Reload Configuration`.
- `Open Log`.
- `Exit`.

The tooltip reports the broker state and configured workspace count, for example `NV-AITools - Running - 2 workspaces`.

No settings form or persistent window is required. `NotifyIcon` supports the icon, tooltip, context menu, and optional concise notifications needed for this design. See the [`NotifyIcon` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon?view=windowsdesktop-10.0).

The application icon is compiled into the executable from one source `.ico` asset. It is not installed as a separate runtime file. The tray icon is always disposed during shutdown so Windows does not retain a stale notification-area entry.

### 10.4 Hidden automatic startup

The intended lifecycle is:

1. [Install.bat](../../Packaging/Install.bat) installs the executable, hidden startup script, default configuration, and skill.
2. Installation creates one per-user automatic-start entry invoking `StartBroker.vbs` through `wscript.exe` in batch mode.
3. `StartBroker.vbs` launches `NV-AITools.exe broker-start` with a hidden window and exits.
4. Installation invokes `broker-start` directly so setup waits for explicit initialization success or failure.
5. `broker-start` launches `broker-run` with `UseShellExecute = false` and `CreateNoWindow = true`.
6. `broker-run` creates the tray icon, validates configuration, activates its workspace watchers, and signals initialization success or failure.
7. `broker-start` exits only after observing that explicit signal.
8. A named per-user mutex prevents duplicate broker instances.
9. [Uninstall.bat](../../Packaging/Uninstall.bat) stops the broker before removing installed files and the automatic-start entry.

`ProcessStartInfo.CreateNoWindow` starts the background child without creating a console window when shell execution is disabled. See the [`CreateNoWindow` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.createnowindow?view=net-10.0).

No Windows service, task scheduler, administrator elevation, tray companion executable, or separate broker executable is required. The only launcher is the installed eight-line script.

### 10.5 Reload and shutdown

`Open config.ini` and `Open Log` use the user's registered text editor through shell execution. These fixed menu actions do not accept request-supplied paths.

`Reload Configuration`:

1. Parses and validates a complete candidate configuration.
2. Reuses the existing runtime for every unchanged canonical workspace root.
3. Creates inactive runtimes for added workspaces without allowing them to claim requests yet.
4. Activates additions and swaps the runtime set only after successful initialization.
5. Stops and disposes removed runtimes after the successful swap.
6. Keeps the previous working state if any validation or initialization step fails.

Reusing unchanged runtimes preserves their serial queues and prevents two execution lanes from operating against the same workspace during reload.

`Exit`:

1. Hides and disposes the tray icon.
2. Stops accepting new requests.
3. Cancels active Plastic, Git, and comparison child processes.
4. Publishes a broker-stopped failure for every request already claimed into `processing`.
5. Disposes watchers and queue resources.
6. Ends the WinForms message loop.

The installer and uninstaller use a controlled broker-shutdown mechanism rather than terminating an arbitrary process by name.

## 11. Logging

The broker maintains one bounded `broker.log` beside `config.ini`.

It records:

- Broker startup and shutdown.
- Configuration validation errors.
- Watched workspace roots.
- Watcher errors and recovery.
- Workspace roots, request identifiers, tool identifiers, queue wait, execution duration, and exit code.
- Cleanup and crash-recovery actions.

It must not record full diff output, source contents, Plastic authentication data, or arbitrary environment variables.

The log is capped at 5 MiB. When a new entry would exceed that limit, the current file replaces one `broker.log.old` file and logging continues in a new `broker.log`. These are internal constants rather than user-facing configuration options.

## 12. Skill Contract

Only one AI skill is installed. It continues to expose the current commands and constraints:

- Invoke `NV-AITools.exe`, never `cm.exe` or `git.exe` directly.
- Use only the documented options.
- Treat stdout as the complete result.
- Treat stderr as diagnostics.
- Check the exit code.
- Do not invoke human-facing BAT wrappers.
- Do not write arbitrary output files unless the user requests an artifact.

The skill does not:

- Start or stop the broker.
- Read `config.ini`.
- Create watcher registrations.
- Know which AI product or Windows identity is running it.
- Request sandbox escalation when the broker is unavailable.

The existing human wrappers remain optional conveniences:

- [RunUvcsStatus.bat](../../Review/RunUvcsStatus.bat)
- [RunPendingChangesDiffs.bat](../../Review/RunPendingChangesDiffs.bat)
- [RunChangesetDiffs.bat](../../Review/RunChangesetDiffs.bat)

Because client stdout, stderr, and exit codes remain stable, those wrappers should require little or no transport-specific change.

## 13. Application Refactoring

The current [Application.RunAsync](../../Application.cs#L9) parses a command, constructs `ProcessRunner`, and dispatches directly to a command implementation. The broker design separates those responsibilities.

Recommended internal boundaries:

1. **Command parsing**
   - Preserve [CommandLine.Parse](../../Cli/CommandLine.cs#L34).
   - Add broker startup commands without exposing arbitrary execution.

2. **Client submission**
   - Serialize the parsed command request.
   - Publish it atomically.
   - Relay the result to the current console contract.

3. **Broker hosting**
   - Load configuration.
   - Manage watchers and queue recovery.
   - Validate broker-side requests independently.
   - Reuse unchanged workspace runtimes during configuration reload.

4. **Command dispatch**
   - Extract the existing command switch into a broker-side dispatcher.
   - Bind the workspace from trusted broker configuration.

5. **Process execution**
   - Keep [ProcessRunner.RunAsync](../../Infrastructure/ProcessRunner.cs#L11) unchanged where practical.
   - Construct and use it only within broker-side dispatch.

6. **Tray lifetime**
   - Replace the top-level statement in [Program.cs](../../Program.cs#L1) with an explicit `[STAThread] Main`.
   - Run the tray host through a WinForms `ApplicationContext` only in `broker-run` mode.
   - Keep console-client execution independent of the message loop.
   - Keep watcher and command work off the WinForms thread.

The broker should capture command diagnostics without globally redirecting `Console` across concurrent work. The first version is serial, but explicit output writers or an execution-result object are preferable to process-wide console mutation.

## 14. Packaging Changes

The portable ZIP continues to contain:

- `NV-AITools.exe`
- `StartBroker.vbs`
- `Install.bat`
- `Uninstall.bat`
- `README.txt`
- One skill template
- Optional human workspace wrappers

New packaging behavior:

- Include a default `config.ini` template.
- Copy it beside the executable only when no existing configuration is present.
- Preserve it during in-place upgrades.
- Create and remove the per-user automatic-start entry.
- Install the fixed hidden startup script beside the executable.
- Stop the old broker before replacing the executable.
- Start the new broker after installation or upgrade.
- Remove the obsolete pre-rename skill during installation and uninstallation.
- Target the Windows Desktop runtime while preserving self-contained single-file publication.
- Compile the application and tray icon into the executable.
- Document the required `.nv-ai-tools/` source-control ignore rule.
- Update [UVCS_Tools_Release_Steps.md](../UVCS_Tools_Release_Steps.md) when implementation begins.

The installation still checks for both Plastic `cm.exe` and `git.exe` as currently implemented in [Install.bat](../../Packaging/Install.bat#L4).

## 15. Explicitly Excluded From Version 1

- Windows services.
- Named pipes, sockets, HTTP, or MCP transport.
- Continuous or periodic directory polling.
- A separate request-client executable.
- A separate broker executable.
- A settings window or configuration-editor GUI.
- Automatic configuration-file watching or implicit hot reload.
- Arbitrary BAT or executable handler registration.
- Tool manifests supplied by workspaces or skills.
- Mutating Plastic operations.
- Remote or network-share reliability guarantees until tested explicitly.
- Overlapping top-level requests within the same workspace, or internal parallelism outside the two diff commands.
- Unbounded cross-workspace parallelism or a user-configurable concurrency limit.

## 16. Implementation Sequence

1. Change the project to `net10.0-windows` with WinForms enabled while retaining console output.
2. Replace the top-level entry point with an explicit `[STAThread] Main`.
3. Extract broker-side command dispatch from [Application.RunAsync](../../Application.cs#L9).
4. Add the minimal `config.ini` parser and workspace validation.
5. Implement upward client workspace discovery through `.nv-ai-tools` without Plastic.
6. Implement request and result DTOs with strict protocol validation.
7. Implement atomic client publication and result consumption.
8. Implement collision-safe random request IDs with create-new publication.
9. Implement broker intake that promptly claims all complete requests.
10. Implement one serial execution lane per workspace and a broker-wide concurrency limit of four.
11. Ensure every concurrent command owns independent temporary and result state.
12. Add watcher setup, startup scanning, error recovery, and crash recovery.
13. Ensure only broker-side code constructs `ProcessRunner`.
14. Add the tray `ApplicationContext`, embedded icon, fixed menu, status tooltip, and error notifications.
15. Add transactional configuration reload with unchanged-runtime reuse.
16. Add singleton broker startup, hidden background launch, controlled cancellation, and claimed-request failure publication.
17. Update installation, upgrade, and uninstallation behavior.
18. Update the single installed skill without changing its public tool commands.
19. Update README and release verification documentation.
20. Validate multiple simultaneous clients in one workspace and across at least four configured workspaces.
21. Validate sandboxed client operation against an authenticated external broker.
22. Validate login startup, tray interaction, configuration reload, broker replacement during upgrade, and clean uninstallation.

## 17. Acceptance Criteria

1. The user can add or remove workspace roots by editing the adjacent `config.ini` and selecting `Reload Configuration` from the tray menu.
2. A default configuration with zero workspaces starts successfully and reports a clear not-configured tray state.
3. Installation creates only one application executable, one hidden startup script, one configuration file, one skill, one bounded log, and one automatic-start entry.
4. Login startup produces a tray icon without leaving a console window open.
5. `broker-start` reports success only after a valid zero-workspace state or all configured workspace watchers are active, and reports initialization failure separately.
6. The tray menu opens configuration and logs, reloads configuration transactionally, and exits cleanly.
7. A failed configuration reload preserves the last valid active configuration.
8. An unchanged workspace runtime and its queue survive configuration reload without a second execution lane.
9. A recovered watcher clears the tray warning only after every failed workspace watcher has recovered.
10. A sandboxed AI can run all three documented commands without direct Plastic authentication and without sandbox escalation.
11. The client resolves a configured workspace from any descendant directory without invoking Plastic.
12. The broker, not the sandboxed client, launches every Plastic and Git process.
13. If the broker is unavailable or the workspace is unconfigured, the client fails clearly within the claim timeout.
14. Existing stdout, stderr, and exit-code behavior remains compatible with the skill and human wrappers.
15. Request publication and broker claiming are atomic.
16. The broker handles both `Created` and `Renamed` notifications.
17. The broker performs an initial scan after enabling each watcher.
18. A watcher buffer overflow or other watcher error causes a full directory reconciliation and watcher recreation.
19. Normal operation performs no periodic directory polling.
20. Requests left by a broker crash are recovered safely on restart.
21. Broker shutdown returns failures for claimed requests and does not leave client processes waiting indefinitely.
22. No request can select an executable, BAT file, workspace, working directory, output path, or raw Plastic/Git argument.
23. Workspace and coordination directories implemented as reparse points are rejected, and validated queue directories cannot be renamed or replaced while the broker runtime is active.
24. Workspace coordination data is ignored by source control.
25. Reinstalling or upgrading preserves the user's `config.ini` and replaces the running broker safely.
26. Uninstalling stops the broker and removes the automatic-start entry, application, configuration, log, and skill without deleting workspace project data.
27. Multiple clients can submit simultaneously in one workspace without request-file collisions, overwrites, lost requests, or overlapping top-level execution within that workspace.
28. Requests in different configured workspaces execute independently, with up to four workspace operations active concurrently.
29. A long-running or failed request in one workspace does not block request intake or available execution capacity for other workspaces.
30. A candidate request-ID collision is detected by create-new file creation and retried without overwriting another request.
31. Concurrent command executions never share temporary directories, result files, mutable output buffers, or process-wide console state.

## 18. Fixed Implementation Decisions

1. Use a per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry for automatic startup.
2. Use a per-user named synchronization event for controlled broker shutdown.
3. Cap the active log at 5 MiB and retain one previous log.
4. Use a twelve-hour client completion timeout after claim.
5. Remove abandoned completed results older than 24 hours during broker startup.
6. Allow mapped or substituted paths but make no remote-watcher reliability guarantee in version 1.
7. Tray `Exit`, update, and uninstall cancel active operations and publish failure results for claimed work.

## 19. Implementation Verification

Completed repository-local verification:

- The application builds on .NET 10 for Windows with zero warnings.
- Configuration and strict request-protocol tests pass.
- A client queue round trip from a workspace descendant passes.
- Two simultaneous requests per workspace across five workspaces remain serial within each workspace and reach exactly four concurrent cross-workspace operations.
- Interrupted `processing` recovery replaces partial output and completes the recovered request.
- Controlled shutdown publishes exit-code `4` results for both active and queued claimed requests.
- The self-contained single-file Windows x64 release ZIP builds and contains the executable, `config.ini`, install/uninstall scripts, generated README, one skill, and optional wrappers.
- The packaged single-file broker starts with zero configured workspaces, creates its configuration and bounded log, exposes its ready signal, and stops through `broker-stop` with a clean shutdown log entry.
- The version 1.1 Release build completes with zero warnings.
- Ordered-pipeline tests reach exactly 16 preparation and 16 transform workers, overlap both stages, preserve source order, apply bounded backpressure, and terminate cleanly on producer failure, consumer failure, and cancellation.
- A real Git fixture proves concatenated per-file add, delete, modify, and identical patches exactly match the former combined-tree `--no-renames` patch.
- CLI and protocol tests reject rename detection with exit code `2` while protocol-version-1 `findRenames=false` requests still round-trip.
- The self-contained `NV-AITools-1.1.0-win-x64.zip` contains version 1.1.0.0 of the executable and the updated skill, generated README, configuration, scripts, and wrappers.
- The packaged version 1.1 broker starts outside the sandbox with zero configured workspaces and stops cleanly through `broker-stop`.
- The version 1.2 Debug and Release integration suites pass, including exact and wildcard filename matching, repeated-filter OR semantics, validation limits, CLI parsing, protocol round trips, and cross-command field rejection.
- The version 1.2 Release publish completes with zero warnings.
- The self-contained `NV-AITools-1.2.0-win-x64.zip` contains file version 1.2.0.0 of the executable and the updated skill, generated README, configuration, scripts, and wrappers.

Remaining receiving-machine acceptance checks:

- Configure a real authenticated UVCS workspace, add `.nv-ai-tools/` to its ignore rules, and invoke all three commands from an AI sandbox without escalation.
- Exercise pending and changeset commands with at least 32 eligible files against the receiving user's authenticated Plastic profile and confirm the intended process peaks and performance improvement.
- Exercise an actual watcher overflow or unavailable-volume recovery event.
- Verify login startup, tray interaction, a failed transactional reload, in-place upgrade with configuration preservation, and clean uninstall against the receiving user's Windows profile.

## 20. Version 1.1 Bounded Parallel Diff Generation

### 20.1 Status and scope

This section records the implemented version 1.1 upgrade. It covers only the per-file preparation and diff-generation phases of `pending-changes-diffs` and `changeset-diffs`. It does not parallelize status discovery, changeset candidate discovery, workspace resolution, top-level requests within one workspace, or any mutating Plastic operation.

The existing broker scheduling model remains in place: one top-level request executes at a time in each workspace, and at most four workspace requests execute broker-wide. The active diff command may internally run bounded read-only child operations. With the selected per-command limits, four simultaneously active workspaces can intentionally reach 64 Plastic processes plus 64 local comparison processes. This is an accepted capacity target, not an accidental unbounded condition, and must be stress-tested before release.

### 20.2 Fixed concurrency contract

Use one internal constant of `16` for each stage. Do not expose another `config.ini` option in this change.

For each command:

1. Run the existing discovery query once and build a stable, sorted list of eligible files.
2. Start at most 16 asynchronous preparation workers. Each worker owns one file at a time and runs that file's Plastic operations sequentially, so the command never has more than 16 active Plastic processes.
3. Publish completed file pairs to a bounded channel with capacity 16 and `FullMode.Wait`.
4. Start at most 16 asynchronous diff workers. A diff worker begins as soon as one complete pair is available, without waiting for all preparation work to finish.
5. Give every work item its stable input index. Workers write only to that index's result slot or uniquely named temporary fragment; a single coordinator assembles stdout, diagnostics, counts, and summary in input order after all workers finish.
6. Do not use `Task.Run`, dedicated threads, shared `StringBuilder` instances, concurrent `TextWriter` calls, process-wide current-directory changes, or fixed temporary filenames. The work is naturally asynchronous because the expensive operations are child processes and file I/O.

Implement the orchestration once as a small internal ordered two-stage pipeline using `System.Threading.Channels`. The helper owns only worker bounds, bounded handoff, ordering, cancellation, and task observation. Command-specific preparation, error policy, formatting, and process arguments remain in the two command classes rather than becoming a general job framework.

### 20.3 Cancellation and completion contract

The pipeline uses a linked cancellation source shared by both stages.

- A fatal producer or consumer failure records the first fatal exception, cancels the linked source, and completes the channel so blocked writes and reads can exit.
- Every channel read, write, asynchronous file operation, and child-process call receives the linked token. Short synchronous path and directory operations are bracketed by cancellation checks.
- Workers check cancellation before claiming another index and before starting another child process.
- The coordinator always observes every worker task before returning or rethrowing; no worker may be abandoned or left with an unobserved exception.
- External broker shutdown cancellation takes precedence over an internally recorded failure, preserving the documented broker-stopped exit behavior.
- The implementation must not rely on a consumer continuing after it has faulted to unblock a bounded-channel producer.

`ProcessRunner` is currently stateless and may be shared by the workers, but its pre-start cancellation check and process-tree termination behavior must remain intact. The pipeline must not add a second semaphore around it because the stage worker counts are the process limits.

### 20.4 Pending changes pipeline

[PendingChangesDiffsCommand.ExecuteAsync](../../Commands/PendingChangesDiffsCommand.cs#L23) keeps workspace resolution and the single `cm status` query ahead of the pipeline.

Preparation behavior:

1. Filter unsupported extensions, directories, moves, local moves, and unchanged checkouts before starting workers; compute the skipped count once.
2. Allocate a unique item directory from the eligible item's stable index.
3. Establish the baseline first: added/private files receive an empty baseline; every other eligible file runs `cm fileinfo` and then `cm getfile` sequentially for that file. Different files may be in either Plastic call concurrently, but the total active Plastic process count remains 16.
4. After that file's baseline is complete, snapshot its current working side into the item directory. Deleted/local-deleted files receive an empty working side. Use cancellable asynchronous copying rather than an uncancellable large `File.Copy`.
5. Publish only after both baseline and working paths are complete and immutable.

Diff behavior:

1. Up to 16 consumers run `fc.exe /n` as soon as pairs arrive.
2. Preserve the existing per-file failure policy: cancellation is rethrown, while tool and unexpected per-file failures become ordered failure sections and processing continues for other files.
3. Preserve the existing path replacement, section format, processed/skipped/failed counts, and maximum exit-code aggregation.
4. Buffer no progress directly through the shared command `TextWriter`. Because broker diagnostics are published only when the request finishes, each result instead carries its diagnostic fragment and the coordinator emits those fragments deterministically.

Parallel preparation changes wall-clock timing while preserving the current per-file baseline-then-working snapshot order. Below the new aggregate output ceiling, the final pending report must remain byte-for-byte stable for the same prepared inputs regardless of completion order.

### 20.5 Changeset pipeline

[ChangesetDiffsCommand.ExecuteAsync](../../Commands/ChangesetDiffsCommand.cs#L24) keeps the Git dependency check and the single `cm log` candidate query ahead of the pipeline. Candidate filtering, case-insensitive deduplication, normalization, safety checks, and sorting remain unchanged.

Preparation behavior:

1. Retain the command-wide temporary `a` and `b` endpoint trees. Each stable candidate owns its normalized repository path under both trees, so workers never write the same endpoint.
2. Run the old-endpoint `cm cat` and then the new-endpoint `cm cat` sequentially for that file. Different files may export concurrently, with no more than 16 active Plastic processes.
3. Treat a documented missing revision as an absent endpoint and ensure no destination file remains. If both endpoints are absent, publish an empty result for that index.
4. Publish only after both endpoint states are final. Binary detection belongs to the consumer stage so preparation can continue feeding the bounded channel.

Diff behavior:

1. Up to 16 consumers inspect the completed pair for NUL bytes and skip binary-like content using the existing rule.
2. For text pairs, invoke one `git diff --no-index --no-renames` process per file from the command temporary root, using trusted relative `a/<repository-path>` and `b/<repository-path>` arguments.
3. Pass Git for Windows `/dev/null` for a genuinely missing endpoint rather than creating an empty stand-in file. A repository-local fixture verified that `NUL` produces no add/delete patch for this invocation, while `/dev/null` exactly preserves the combined-tree new-file and deleted-file output. Existing empty files remain real files.
4. Preserve the requested diff algorithm and the current `core.autocrlf=false`, `core.safecrlf=false`, `--no-ext-diff`, `--no-textconv`, and `--no-prefix` protections.
5. Treat Git exit codes `0` and `1` as success. Any other export, binary-read, or Git failure is fatal for `changeset-diffs`, cancels both stages, and returns no partial patch, matching the current command-level failure policy.
6. Concatenate successful patch fragments in the original sorted candidate order. Completion order must never affect stdout.

Both diff commands use one 64 Mi-character aggregate command ceiling rather than an independent allowance for every child process. `ProcessRunner` reserves that shared budget incrementally as stdout and stderr chunks are read, so a worker is stopped as soon as either its per-process limit or the command-wide limit would be exceeded. The lower aggregate ceiling also bounds the duplication inherent in assembling and publishing the final in-memory stdout string. Temporary indexed fragments remain a possible future optimization, but the broker result contract is not redesigned in this change.

### 20.6 Rename detection transition

Per-file Git invocations cannot detect a delete/add rename across different candidates. Therefore rename detection is disabled rather than silently producing incomplete rename results.

1. Remove `--find-renames` from displayed command usage and the installed skill.
2. Make [CommandLine.Parse](../../Cli/CommandLine.cs#L34) reject `--find-renames` with a specific exit-code `2` diagnostic explaining that parallel per-file diffs do not support it.
3. Retain the nullable `findRenames` queue field for protocol-version-1 compatibility, but make [QueueProtocol.ParseRequest](../../Broker/QueueProtocol.cs#L61) reject a value of `true`. Existing and new clients continue to send `false`.
4. Keep the current command-wide `a` and `b` trees and move the final combined-tree Git invocation and argument builder into a clearly named retained method behind a `static readonly` disabled switch. Add a comment that this code must be kept because one Git comparison over both complete trees is the correct restoration path for cross-file rename detection. When disabled, consumers run per-file Git. The retained branch may binary-filter the prepared trees and then run the single combined comparison only after all preparation is complete.
5. Keep `--no-renames` explicit in every active per-file Git invocation.

This intentionally changes one public option. All other command arguments, exit codes, stdout ownership, and broker request protocol version remain unchanged.

### 20.7 Output ownership and temporary data

- One command owns one [TempDirectory](../../Infrastructure/TempDirectory.cs#L3).
- Every pending item and output fragment owns a deterministic subdirectory based on its stable index. Changeset endpoints remain under the existing command-wide `a` and `b` roots and use the already validated normalized repository path.
- Prepared pairs are immutable after channel publication.
- Each result slot and optional fragment file has exactly one writer.
- Only the coordinator reads result slots and assembles output.
- Final assembly validates that every eligible index reached exactly one terminal result.
- Existing repository-path containment checks remain mandatory before constructing endpoint paths.
- Cancellation and exceptions still dispose the command temporary directory through the existing scoped owner.

### 20.8 Implementation record

1. Added the small ordered two-stage pipeline helper and focused concurrency, ordering, backpressure, failure, and cancellation tests.
2. Converted pending-change preparation and `fc.exe` comparison while preserving its continue-on-file-error contract and output order.
3. Converted changeset endpoint export and Git comparison with command-level fail-fast behavior.
4. Disabled rename requests at both external parsing boundaries and retained the commented combined-tree implementation behind the disabled switch.
5. Added a real Git fixture proving that per-file add, delete, modify, and identical results concatenate to the exact former combined-tree `--no-renames` output. The fixture also established that Git for Windows requires `/dev/null`, not `NUL`, for missing endpoints in this invocation.
6. Updated the installed skill, package README, command usage, release steps, and this document for version 1.1.

### 20.9 Validation gate

Automated pipeline tests must prove:

- Preparation concurrency reaches but never exceeds 16.
- Diff concurrency reaches but never exceeds 16.
- A consumer starts before all preparation completes.
- Reverse and randomized completion orders still produce exact input ordering.
- A controlled saturation test with at least 64 items proves that publication applies backpressure after 16 queued pairs; code review confirms `BoundedChannelOptions(16)` and `FullMode.Wait` are used.
- Fewer than 16 items, zero eligible items, and more than 32 items complete without special cases.
- Producer failure, consumer failure, cancellation while a producer is blocked, and cancellation while a consumer is blocked all terminate within the test timeout with every task observed.
- Pending per-file failures continue and preserve ordered counts; changeset failures cancel and return no partial patch.

Command and integration validation must prove:

- Pending added, changed, deleted, local-deleted, private, identical, unsupported, moved, failed-export, and failed-compare cases match the existing ordered report contract.
- Changeset add, modify, delete, empty-file, identical-file, both-missing, binary-like, unsafe-path, failed-export, and failed-Git cases have correct output and exit behavior.
- Per-file Git output matches the existing combined-tree `--no-renames` output for a fixed multi-file fixture, apart from explicitly documented ordering or header differences that are reviewed before acceptance.
- CLI and crafted queue requests both reject rename detection with exit code `2`; ordinary protocol-version-1 requests with `findRenames=false` still round-trip.
- Cancellation prevents new process starts, terminates active process trees, and fits within broker shutdown behavior.
- One authenticated command reaches the intended 16 Plastic and 16 diff-process peaks without exceeding either limit.
- Four configured workspaces can exercise the accepted 64-Plastic plus 64-diff worst case without queue corruption, broker starvation, unacceptable machine pressure, or Plastic authentication failures.
- Authenticated results are content-equivalent to the captured sequential baselines and are materially faster on representative workloads.
- Build, existing broker integration tests, hidden startup smoke test, install/upgrade/uninstall checks, and release ZIP inspection still pass.

### 20.10 Release decision and residual risks

Do not package this enhancement merely because unit tests pass. Release requires authenticated concurrency validation against the actual Plastic client, server, and user profile because the public `cm` documentation does not guarantee that 16 simultaneous CLI processes sharing one authentication context will behave well.

The accepted design can create up to 128 child processes across four active workspaces. If stress validation shows unacceptable CPU, memory, disk, server, or authentication pressure, lower the single internal per-stage constant and repeat the same tests; do not add an unplanned global scheduler or another user-facing setting during this change.

Per-file changeset comparison deliberately has no rename detection. Restoring it later requires intentionally re-enabling and revalidating the retained combined-tree path, not adding heuristics that try to infer renames from independently generated patches.

The queue and broker still publish one final stdout string, so aggregate output remains memory-resident during final assembly and publication. The 64 Mi-character command ceilings bound but do not remove that peak-memory cost. A streamed-result protocol would be a separate protocol and broker design project.

## 21. Version 1.2 Pending Filename Filters

### 21.1 Public contract

`pending-changes-diffs` accepts zero or more repeatable `--file-filter <glob>` options. Filters are case-insensitive, match only the filename basename in any folder, support `*` and `?`, and combine with OR. Exact filenames are ordinary patterns without wildcards. Duplicate basenames in different directories intentionally both match. Omitting the option preserves the complete pending-diff behavior, while a valid filter matching no files returns the existing successful zero-processed report.

Directory matching is intentionally outside version 1.2. Both `/` and `\` are rejected rather than being interpreted as path syntax. Status discovery still reads the full workspace once; filtering eliminates the expensive per-file Plastic, snapshot, and comparison work, not the initial `cm status` query.

### 21.2 Validation and queue contract

[PendingChangesDiffsRequest](../../Cli/CommandLine.cs#L10) is the single owner of filter validation, cloned storage, and matching. It accepts at most 32 filters, 255 characters per filter, and 1024 total filter characters. Empty or whitespace-only filters and Windows-invalid filename characters are rejected, except for the supported `*` and `?` wildcards.

[QueueProtocol](../../Broker/QueueProtocol.cs#L22) adds the optional `fileNameFilters` array to pending requests. Protocol version remains 1 because this is an additive reader extension and an unfiltered request retains the previous empty-arguments JSON. The broker rejects the field on status and changeset requests and rejects null or invalid pending filter entries before dispatch.

### 21.3 Execution and reporting

[PendingChangesDiffsCommand.ExecuteAsync](../../Commands/PendingChangesDiffsCommand.cs#L23) checks the filter while constructing its eligible item list. Excluded source entries increment the existing skipped count and never receive a temporary directory, `cm fileinfo`, `cm getfile`, workspace copy, or `fc.exe` comparison. The version 1.1 ordered two-stage pipeline, deterministic output, concurrency limits, per-file failure handling, report format, and exit codes remain unchanged.

### 21.4 Validation gate

Repository-local tests cover unfiltered behavior, exact case-insensitive basename matching, `*` and `?`, repeated-filter OR semantics, CLI preservation, filtered and unfiltered queue round trips, all filter limits, null entries, and rejection of the pending-only field by other commands. Receiving-machine acceptance must additionally confirm with an authenticated Plastic workspace that excluded files start no per-file Plastic operations and that a valid unmatched filter succeeds with zero processed files.

## 22. Version 1.3 Serialized UVCS and Batched Changesets

Version 1.2 authenticated testing showed that multiple simultaneous `cm cat` processes could return exit code zero without creating every requested destination. Version 1.3 replaces that unsafe concurrency assumption with one broker-owned [UvcsRunner](../../Infrastructure/UvcsRunner.cs) and one active UVCS process maximum across every configured workspace. Workspace resolution, status, pending baselines, changeset discovery, and changeset downloads all share this cancelable gate; Git and `fc.exe` remain independently concurrent.

[ChangesetDiffsCommand](../../Commands/ChangesetDiffsCommand.cs) discovers endpoint changes with one formatted `cm diff cs:<from> cs:<to>` call. It maps add/change/delete rows directly and maps every file move to a source deletion plus destination addition. Moved-directory rows fail explicitly because one directory row cannot safely represent descendant file moves. Supported paths are normalized, containment-checked, sorted case-insensitively with an ordinal tiebreaker, and kept distinct for case-only moves.

Content is downloaded with the multi-revision `cm getfile` collection introduced in Unity Version Control 11.0.16.8411. Each sequential process contains at most 16 revision/destination pairs and stays within a conservative 24 KiB escaped command-line budget. A logical old/new pair is never split. Semicolon paths and collection-oversized items use single-revision calls through the same gate. There are no automatic retries. A batch is released only after a zero exit and existence checks for every expected destination; otherwise the entire changeset request fails without a partial patch.

[OrderedBatchPipeline](../../Infrastructure/OrderedBatchPipeline.cs) has one producer and a bounded 16-item channel feeding up to 16 local Git consumers. Comparisons of one completed batch overlap the next UVCS download, result slots remain indexed, and missing, duplicate, or out-of-range publications fail explicitly. Pending diffs retain their existing per-file reporting and filename-filter contract; their preparation workers may wait concurrently for the shared UVCS gate while local copies and comparisons continue.

Installation runs the packaged `broker-check-dependencies` preflight before stopping or replacing an existing broker. Broker startup and configuration reload use the same checker before readiness. Repository-local tests cover the one-process gate, cancelable waiting, gate release after failure, version parsing, direct metadata mapping, move expansion, batch limits and fallback, batch-pipeline ordering and failure validation, plus all previous integration coverage. Release remains gated on repeated authenticated changeset 21366-to-21380 success, stable patch hashes, one-process observation across multiple workspaces, path edge cases, cancellation, upgrade, and uninstall checks in [the release steps](../UVCS_Tools_Release_Steps.md).
