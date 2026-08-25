# NV-AITools Implementation Outline

*Concise implementation record for the read-only queue client and authenticated tray broker.*

The detailed contracts and rationale live in [AI_Agnostic_Broker_Working_Design.md](./plan/AI_Agnostic_Broker_Working_Design.md).

## 1. Runtime Shape

One self-contained `NV-AITools.exe` has three roles:

1. Normal `status`, `pending-changes-diffs`, and `changeset-diffs` invocations parse the public command line, discover the nearest configured `.nv-ai-tools` directory, publish a typed request, and relay the broker result to stdout, stderr, and the process exit code.
2. `broker-start` launches a hidden background child, waits for explicit initialization success or failure, and exits.
3. `broker-run` owns the authenticated command execution, filesystem watchers, per-workspace queues, log, and system-tray lifetime.

Only broker-side code constructs `ProcessRunner` or launches Plastic, Git, and comparison processes. Client-side workspace discovery never invokes Plastic or reads `config.ini`.

## 2. Configuration and Lifetime

The installed application directory contains:

```text
NV-AITools.exe
StartBroker.vbs
config.ini
broker.log
broker.log.old
```

`config.ini` supports only a `[Workspaces]` section with absolute numbered `PathN` entries. Zero entries is a valid first-run state. The tray menu opens configuration and logs, reloads a complete candidate configuration transactionally, or exits the broker.

The broker is single-instance per installation and Windows user. A named mutex enforces ownership; named ready and startup-failure events report initialization state; and a named stop event provides controlled shutdown for tray Exit, upgrades, and uninstallation. Installation registers the tiny `StartBroker.vbs` hidden launcher under the current user's `Run` key; no service, elevation, scheduled task, or second executable is required.

## 3. Queue Contract

Each configured workspace receives:

```text
.nv-ai-tools/
  requests/
  processing/
  results/
```

The client uses a cryptographically random 128-bit lowercase hexadecimal request ID, create-new temporary file creation, a flushed write, and an atomic rename to publish `<id>.request`. Request JSON contains only protocol version, ID, fixed tool name, and that tool's typed arguments. The broker binds the workspace from the watcher that claimed the file; request content cannot choose a workspace, executable, output path, working directory, or raw command arguments.

The broker atomically moves each request to `processing`, validates its size and schema, and queues it to that workspace's serial execution lane. Different workspace lanes share a broker-wide four-operation semaphore. Result stdout and stderr are published first; `<id>.result` is renamed into place last as the completion marker. The client deletes only the exact files belonging to its request after consumption.

The broker rejects filesystem reparse points for the workspace and every coordination directory, verifies each directory's resolved location, and keeps non-delete-sharing handles open for the workspace runtime's lifetime. This prevents an untrusted client from renaming or replacing a validated queue path before a broker write or delete. Add `.nv-ai-tools/` to each configured workspace's source-control ignore rules.

## 4. Watcher Reliability

Each request directory has one narrow `FileSystemWatcher` for filename-only `Created` and `Renamed` events. Callbacks only coalesce a drain signal. The intake task enumerates and atomically claims every complete request; long-running commands execute on a separate workspace lane.

The watcher is enabled before the initial enumeration. On any watcher error, the runtime disposes the watcher, reconciles the durable directory, recreates monitoring with capped retry delays, scans again, and restores healthy tray status after every failed workspace watcher has recovered. There is no broker polling loop in normal operation. On restart, interrupted processing files are returned to requests because all current operations are read-only and safe to retry.

## 5. Output and Failure Contract

| Channel or code | Contract |
|---|---|
| stdout | Complete status JSON, pending-change report, or changeset patch. |
| stderr | Progress, warnings, and actionable diagnostics. |
| 0 | Success, including a valid empty result. |
| 2 | Invalid command or request input. |
| 3 | Broker, workspace, or dependency unavailable. |
| 4 | Plastic, Git, comparison, timeout, or broker-stop interruption. |
| 5 | Parsing, temporary-file, protocol, or internal failure. |

Claimed requests receive a terminal failure result during controlled shutdown, including requests waiting in a workspace lane or for global capacity. Cancellation escapes per-file processing immediately instead of continuing across the remaining files. Unclaimed requests remain durable for the next broker start.

## 6. Version 1.1 Diff Pipelines

`pending-changes-diffs` and `changeset-diffs` use one ordered two-stage pipeline. Each active command runs at most 16 preparation workers, hands complete file pairs through a bounded 16-item channel, and runs at most 16 comparison workers. Plastic calls remain sequential within one file, limiting each command to 16 active Plastic processes while allowing comparisons to begin before all files are exported.

Workers own unique temporary paths and indexed result slots. The coordinator alone assembles stdout and diagnostics in candidate order. Pending comparisons retain continue-on-file-error behavior; changeset comparisons fail fast and cancel both stages. Cancellation stops new child-process starts and unblocks channel readers and writers.

Changeset diffs run one `git diff --no-index --no-renames` process per text file. `/dev/null` represents a missing endpoint and is verified against the former combined-tree output for additions and deletions. Public and protocol rename requests are rejected because independent per-file comparisons cannot detect cross-file renames. The disabled combined-tree Git path remains in source as the correct restoration point for future rename support.

## 7. Version 1.2 Pending Filename Filters

`pending-changes-diffs` accepts repeatable `--file-filter <glob>` options. Each filter is matched case-insensitively against the path's filename basename, supports only `*` and `?`, and combines with other filters using OR. Omitting filters preserves the complete version 1.1 pending-diff behavior; a valid unmatched filter is a successful zero-processed report.

[PendingChangesDiffsRequest](../Cli/CommandLine.cs#L10) owns validation, immutable filter storage, and matching so both CLI and broker requests use one contract. Requests are limited to 32 filters, 255 characters per filter, and 1024 total filter characters. Empty filters and Windows-invalid filename characters other than the two wildcards are rejected, including both directory separators.

[QueueProtocol](../Broker/QueueProtocol.cs#L22) carries the optional filter array without changing protocol version 1. Unfiltered serialized requests retain the previous empty-arguments shape, while status and changeset requests reject the pending-only field. [PendingChangesDiffsCommand.ExecuteAsync](../Commands/PendingChangesDiffsCommand.cs#L23) applies matching after status discovery but before temporary files, per-file Plastic calls, copies, and comparisons. Excluded eligible entries contribute to the existing skipped count.

## 8. Version 1.3 Serialized UVCS and Batched Changesets

The broker owns one [UvcsRunner](../Infrastructure/UvcsRunner.cs) for its lifetime. Every workspace resolution, status query, pending baseline query, changeset metadata query, and content download passes through its cancelable one-process gate. Local `git.exe` and `fc.exe` comparisons remain outside the gate. Broker startup and configuration reload require Unity Version Control 11.0.16.8411 or newer before readiness because that release introduced multi-revision `getfile` collections.

[ChangesetDiffsCommand](../Commands/ChangesetDiffsCommand.cs) now uses one direct endpoint `cm diff` instead of aggregating changeset logs. Added, changed, and deleted files map directly to logical output items. File moves map to a deletion at the source path and an addition at the destination path so the intentionally disabled rename detection cannot hide a pure move. A moved directory fails explicitly because its single metadata row does not provide a safe per-file representation of descendant moves.

Sorted logical changes are packed into sequential `cm getfile` batches with at most 16 revision/destination entries and a conservative 24 KiB command-line budget. Old and new sides of one logical item are never split. Paths containing the collection delimiter, or an item too large for the collection budget, use the single-revision form through the same gate. A batch publishes nothing until the process succeeds and every requested destination exists.

The changeset command uses [OrderedBatchPipeline](../Infrastructure/OrderedBatchPipeline.cs): one batch producer feeds a bounded 16-item channel and up to 16 local Git consumers. This overlaps the next UVCS batch with comparisons of the previous batch while preserving deterministic result order and command-level fail-fast behavior. The pending command keeps its version 1.2 output and per-file failure policy, but its UVCS calls are globally serialized.

Repository-local command tests run the complete changeset coordinator through a fake UVCS boundary. They cover a multi-revision collection, semicolon-safe single downloads, final Git patches, an exit-zero incomplete batch, and a nonzero batch that writes diagnostics to both stdout and stderr.

## 9. Packaging and Trust

The portable ZIP contains the executable, hidden startup script, default configuration, install and uninstall scripts, generated README, one AI skill, and optional human wrappers. Installation checks for both `cm.exe` and `git.exe`, runs the packaged minimum-version preflight before any installation mutation, preserves an existing `config.ini`, removes the obsolete pre-rename skill, stops the previous broker before replacement, installs per user, registers hidden login startup, and starts the broker. Uninstallation stops the broker and removes the application, startup script, configuration, logs, startup entry, current skill, and obsolete skill without touching workspace data.

The per-user application directory is writable by the current user. The trust model therefore requires AI agents to run in a sandbox that cannot modify the installed executable, configuration, or skill. Using an unrestricted AI means accepting that it can replace those trusted components.
