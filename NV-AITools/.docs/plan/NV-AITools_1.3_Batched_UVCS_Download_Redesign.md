# NV-AITools 1.3 Batched UVCS Download Redesign

Implementation status: source implementation and repository-local automated validation are complete. Installed authenticated acceptance remains required before release packaging.

## Status

Implementation-ready plan. No production code is changed by this document.

This plan supersedes the version 1.1 changeset-export concurrency design after the authenticated failure confirmed in the [changeset export investigation](../investigations/changeset-diffs-export-failure/changeset-diffs-export-failure-result.md).

## Decision Summary

Version 1.3 should replace one-process-per-revision changeset exports with Unity Version Control's supported multi-revision `getfile` command.

The initial correctness configuration is:

| Concern | Version 1.3 decision |
|---|---:|
| Broker-wide active `cm` processes | 1 |
| Revision downloads per batch | At most 16 |
| Complete pairs buffered for comparison | At most 16 |
| Local Git comparison workers | At most 16 |
| Automatic Plastic retries | 0 |
| Partial changeset patch on failure | Never |

One authenticated UVCS process is allowed broker-wide, including metadata and status operations. Local Git and `fc.exe` work remains parallel and may overlap UVCS work from another batch or workspace.

This deliberately does not begin with four concurrent batched `cm` processes. Batching is documented by Unity; safe shared-profile process concurrency is not. A higher process limit may be benchmarked only after the one-process implementation is stable and authenticated stress tests demonstrate identical results.

## Goals

1. Make `changeset-diffs` reliable for large changesets and ranges.
2. Use the UVCS multi-revision download primitive introduced in 11.0.16.8411.
3. Preserve the rolling pipeline: compare complete pairs while later batches download.
4. Preserve deterministic patch and diagnostic order.
5. Preserve fail-fast, no-partial-patch changeset behavior.
6. Remain safe when several AI clients use several configured workspaces.
7. Keep the broker protocol, public command line, configuration file, and skill contract unchanged.
8. Minimize new abstractions and user-facing settings.

## Non-goals

- Do not add a user-configurable concurrency or batch-size setting.
- Do not restore rename detection in this change.
- Do not adopt `cm diff --download`; its old/new layout, completion contract, and failure granularity are insufficiently documented for the existing explicit-pair pipeline.
- Do not add speculative retries, exponential backoff, or recursive batch splitting to the fail-fast changeset command.
- Do not batch the pending-changes baseline flow in version 1.3. The global UVCS process gate makes it safe; grouped pending `fileinfo` and per-file failure isolation can be a later measured optimization.
- Do not treat exit code 0 plus a missing destination as success or as an absent revision.

## Supported UVCS Contract

Unity Version Control 11.0.16.8411 added a collection form for `getfile` and `cat`: one CLI invocation accepts whitespace-separated arguments in `revspec;destination` form and downloads several revisions. See the [official 11.0.16.8411 release notes](https://www.plasticscm.com/download/releasenotes/11.0.16.8411).

The current [GETFILE reference](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/getfile) documents revision downloads, `--raw`, and `--file`; the release notes define the collection form. The [DIFF reference](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/diff) defines changeset-to-changeset comparison, repository paths, statuses, source and destination paths, and revision IDs.

Version 1.3 therefore requires UVCS client 11.0.16.8411 or newer. The packaged executable and broker must run [`cm version`](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/version), parse the numeric version, and return an actionable dependency diagnostic when the client is too old.

The first receiving-machine validation must explicitly prove that the installed client accepts collection arguments together with `--raw`. Do not release based only on documentation or mocked process tests.

## Architecture

```text
AI clients and workspace queues
              |
              v
    existing per-workspace lanes
              |
              v
    broker-owned UVCS process gate (1)
              |
       +------+----------------+
       |                       |
       v                       v
metadata/status cm       one multi-getfile batch
                                |
                     validate every destination
                                |
                     publish complete file pairs
                                |
                                v
                     bounded pair channel (16)
                                |
                                v
                     local Git workers (up to 16)
                                |
                                v
                     indexed deterministic assembly
```

The broker's existing four-workspace capacity remains. A workspace can perform local comparison while another workspace owns the UVCS slot. Requests waiting for UVCS remain cancelable and do not occupy operating-system threads.

## Core Invariants

1. At most one NV-AITools-started `cm` process exists at any time across the broker.
2. Every batch contains all downloadable sides for each included logical change; a modified pair is never split across batches.
3. A batch publishes nothing until `cm getfile` exits successfully and every expected destination exists.
4. Consumers only receive complete, immutable endpoint state.
5. Every prepared item carries its stable sorted index; only the coordinator assembles final output and diagnostics.
6. Any changeset metadata, download, validation, binary-read, or Git failure cancels the pipeline and returns no patch.
7. Cancellation stops gate waits, the active child process, channel publication, and local comparison workers.
8. Temporary paths remain request-owned and are removed after every success, failure, or cancellation.

## 1. Broker-owned UVCS runner

Add one small infrastructure service, tentatively `UvcsRunner`, that owns a `SemaphoreSlim(1, 1)` and delegates actual process execution to [ProcessRunner.RunAsync](../../Infrastructure/ProcessRunner.cs#L47).

`UvcsRunner.RunAsync` should expose the same timeout, output-limit, cancellation, and command-budget inputs needed by current `cm` call sites, but should accept only UVCS arguments and always invoke `cm`. This prevents callers from accidentally holding the UVCS gate around Git or `fc.exe`.

[BrokerHost](../../Broker/BrokerHost.cs#L7) owns and disposes this service. Inject the same instance into:

- [CommandDispatcher](../../Broker/CommandDispatcher.cs#L7),
- configuration-time [WorkspaceResolver](../../Infrastructure/WorkspaceResolver.cs#L3),
- request-time workspace resolution,
- [StatusReader](../../Infrastructure/StatusReader.cs#L5), and
- both diff commands.

Keep [ProcessRunner](../../Infrastructure/ProcessRunner.cs#L45) for Git and `fc.exe`. Do not use a static semaphore or a process-wide hidden singleton.

The gate is acquired immediately before `ProcessRunner.RunAsync("cm", ...)` and released in `finally`. A canceled waiter must not start a child process. Broker shutdown already supplies the cancellation token that kills an active child through `ProcessRunner`.

## 2. Dependency capability check

Put version execution and parsing in one C# capability checker. Expose it through an internal console command such as:

```text
NV-AITools.exe broker-check-dependencies
```

This command runs locally, does not use the workspace queue, performs no mutation, and returns the normal dependency failure exit code with a concise stderr diagnostic.

`Install.bat` must invoke the packaged executable's dependency check after its `where cm.exe` and `where git.exe` checks but before stopping the existing broker, copying files, changing startup registration, or removing an old installation. An unsupported UVCS client therefore leaves the existing installation untouched.

During [BrokerHost.StartAsync](../../Broker/BrokerHost.cs#L24), run the same checker again before configuration readiness so a later client downgrade is detected:

1. Run `cm version` through `UvcsRunner`.
2. Extract the first four-component numeric version.
3. Require `>= 11.0.16.8411`.
4. Report missing, malformed, or older clients as dependency/workspace failure.
5. Log the accepted UVCS version once.

Do not duplicate fragile numeric parsing in `Install.bat`. Both the installer preflight and broker startup call the same executable-owned checker. Update the generated README and install error guidance to state the minimum version.

This makes an unsupported client fail immediately instead of waiting for the first large changeset request.

## 3. Discover endpoint changes directly

Replace the `cm log` union in [ChangesetDiffsCommand.GetCandidatesAsync](../../Commands/ChangesetDiffsCommand.cs#L205) with one direct endpoint comparison:

```text
cm diff cs:<from> cs:<to>
  --repositorypaths
  --format={status}<US>{type}<US>{path}<US>{srccmpath}<US>{dstcmpath}{newline}
```

`<US>` is the existing U+001F field separator. Windows paths cannot contain this control character.

Parse only UVCS statuses and item types defined by the official DIFF contract. Normalize and containment-check every repository path using the current path rules.

Map rows into logical changes as follows:

| UVCS status | Logical output items | Old side | New side |
|---|---|---|---|
| `C` changed | one item at `{path}` | `{path}#cs:<from>` | `{path}#cs:<to>` |
| `A` added | one item at `{path}` | absent | `{path}#cs:<to>` |
| `D` deleted | one item at `{path}` | `{path}#cs:<from>` | absent |
| `M` moved | delete at `{srccmpath}`, then add at `{dstcmpath}` | source only | destination only |

Split moves into delete/add items because rename detection remains disabled. Pairing the old and new names would make a content-identical move disappear from per-file Git output.

Apply the supported-extension filter independently to each logical path. This preserves sensible behavior for moves into or out of a supported extension. Accept file (`F`), binary (`B`), and symlink (`S`) rows; retaining the existing command without `--symlink` preserves its target-content behavior. Ignore non-move directory (`D`) and Xlink (`X`) rows. Reject moved directories because their single metadata row cannot safely represent descendant file moves. Also reject malformed rows, missing move paths, unknown statuses/types, and conflicting exact duplicate logical items rather than guessing.

Sort logical items by repository path using case-insensitive ordering with ordinal ordering as a deterministic tiebreaker. Preserve separate case-only move paths; do not collapse them in a case-insensitive set.

Using direct `cm diff` changes only candidate discovery, not final patch meaning. Paths touched and later restored between the endpoints are no longer downloaded, because they cannot contribute to the endpoint patch. Equal endpoints return an empty result immediately.

## 4. Build safe multi-revision batches

Retain the existing, proven `serverpath:/<path>#cs:<changeset>` revision form rather than adding repository/server discovery solely to use `revid:`. Endpoint changesets are immutable and the current selector already works for individual downloads.

For every logical change, create zero, one, or two download entries:

```text
serverpath:/<repository-path>#cs:<endpoint>;<absolute-temp-destination>
```

Pack sorted logical changes into batches with these rules:

1. At most 16 revision entries per batch.
2. Never split the old/new entries of one logical item.
3. Keep an estimated escaped command line at or below 24 KiB, leaving headroom below the Windows [32,767-character process limit](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw).
4. Flush the current batch before adding an item that would exceed either limit.
5. Create destination parent directories before starting UVCS.

Use [ProcessStartInfo.ArgumentList](../../Infrastructure/ProcessRunner.cs#L69) through the existing runner. Never construct a manually quoted command string.

The batch builder should conservatively account for Windows quoting expansion, not merely sum raw argument lengths. The collection grammar uses semicolon as its revision/destination delimiter and does not document escaping. If either half of a collection argument contains a semicolon, use the legacy single-revision `getfile <revspec> --file=<destination> --raw` form for that logical item. It still runs through the one-process broker gate and is therefore safe. Use the same single-item fallback when one argument cannot fit the 24 KiB batch budget.

Do not expose batch size as configuration. `16` is an implementation constant with focused tests.

## 5. Execute and validate one batch

The normal batch command is:

```text
cm getfile
  "<revspec>;<destination>"
  "<revspec>;<destination>"
  ...
  --raw
```

After the process exits:

1. If the exit code is nonzero, fail the whole changeset command with exit code 4 and include trimmed stdout/stderr.
2. If the exit code is zero, verify every expected destination exists.
3. If any are missing, fail the whole command with exit code 5 and list every missing logical path, endpoint, revision specification, and non-empty child diagnostic.
4. Publish no item from a failed or incomplete batch.
5. Do not retry automatically in version 1.3.

Partial files from a failed batch remain inside the request's [TempDirectory](../../Infrastructure/TempDirectory.cs#L3) and are removed during normal command cleanup. No partial content reaches Git.

## 6. Replace the changeset preparation scheduler

The current [OrderedParallelPipeline](../../Infrastructure/OrderedParallelPipeline.cs#L6) assumes many independent one-item producers, so it is the wrong shape for one sequential producer that emits several prepared items per batch.

Add a narrowly scoped ordered batch pipeline with:

- one asynchronous batch producer,
- a bounded channel of 16 indexed prepared items,
- up to 16 asynchronous transform consumers,
- an indexed result array,
- first-failure capture,
- linked cancellation, and
- deterministic result return.

Suggested contract:

```csharp
RunAsync<TBatch, TPrepared, TResult>(
    IReadOnlyList<TBatch> batches,
    int resultCount,
    Func<TBatch, CancellationToken, Task<IReadOnlyList<IndexedItem<TPrepared>>>> prepareBatch,
    Func<int, TPrepared, CancellationToken, Task<TResult>> transform,
    CancellationToken cancellationToken)
```

`IndexedItem<T>` carries the stable logical-item index. The producer awaits one batch, validates it completely, then writes its prepared items to the channel. Consumers start Git comparison immediately after the first batch succeeds while the producer downloads the next batch.

The helper must reject out-of-range or duplicate indices and verify that every result index was produced exactly once before returning. A missing prepared item must never leave a default result that could be assembled silently.

Keep the existing pipeline for `pending-changes-diffs` in version 1.3. Do not contort one helper into serving incompatible producer models.

## 7. Preserve comparison and assembly behavior

Keep the current per-file behavior in [ChangesetDiffsCommand.CompareAsync](../../Commands/ChangesetDiffsCommand.cs#L133):

- inspect both present sides for NUL bytes,
- delete and skip binary-like content,
- invoke Git with the requested algorithm,
- accept Git exit codes 0 and 1,
- retain no cross-file rename detection, and
- store each patch and diagnostic in its indexed result.

Only the coordinator writes the buffered command `TextWriter` and final `StringBuilder`. The broker does not stream diagnostics to the client before completion, so worker progress writes provide no user benefit and would reintroduce shared-writer synchronization.

## 8. Pending changes behavior

[PendingChangesDiffsCommand](../../Commands/PendingChangesDiffsCommand.cs#L9) keeps its public behavior, filename filters, continue-on-file-error policy, and existing ordered pipeline.

Its `cm status`, `cm fileinfo`, and `cm getfile` calls must use the shared `UvcsRunner`. The 16 preparation workers may wait concurrently, but only one starts a UVCS process. Local workspace copies and `fc.exe` comparisons can still overlap.

This is intentionally conservative. Unity documents that `fileinfo` accepts multiple paths and recommends grouping them, but batching pending baselines while retaining accurate per-file failure reporting requires a separate plan. Do not mix that larger change into the changeset repair.

## 9. Multi-workspace behavior

The existing per-workspace serial lanes and four-workspace broker capacity remain unchanged.

Example:

```text
Workspace A: UVCS batch ---- Git comparisons --------------------
Workspace B: waits --------- UVCS status ---- local comparisons -
Workspace C: local fc work ------------------ waits for UVCS -----
Workspace D: queued/local work ----------------------------------
```

The UVCS gate protects the single authenticated user profile across all workspaces. The existing workspace lanes continue to protect workspace-specific ordering. Local comparisons from different workspaces remain bounded by their current per-command limits and the broker-wide four-command capacity.

## 10. Failure and cancellation contract

### Changeset command

- Metadata failure: no downloads, no patch.
- Batch nonzero exit: no items from that batch, cancel consumers, no patch.
- Exit zero with missing files: internal failure with exact endpoints and child diagnostics, no patch.
- Binary file: skip that logical item as today.
- Git failure: cancel the active or waiting UVCS work and other consumers, no patch.
- Broker shutdown: cancel gate waits and kill the active child, return the documented broker-stopped result.

### Pending command

- Preserve current per-file failure reporting and aggregate exit code.
- Waiting for the UVCS gate remains cancelable.
- Do not convert pending behavior to command-level fail-fast.

No path may be reported as successfully exported solely because `cm` returned zero.

## 11. File-level implementation map

### New files

- `Infrastructure/UvcsRunner.cs` - broker-owned single-flight UVCS execution and minimum-version validation.
- `Infrastructure/OrderedBatchPipeline.cs` - one batch producer, bounded indexed channel, parallel ordered transforms.

### Modified production files

- [BrokerHost.cs](../../Broker/BrokerHost.cs) - own, initialize, inject, and dispose `UvcsRunner`; use it for configured-workspace validation.
- [Program.cs](../../Program.cs) and broker application dispatch - add the internal non-queued `broker-check-dependencies` preflight using the same version checker as broker startup.
- [CommandDispatcher.cs](../../Broker/CommandDispatcher.cs) - accept the shared UVCS runner and pass it to commands and workspace infrastructure.
- [WorkspaceResolver.cs](../../Infrastructure/WorkspaceResolver.cs) - use `UvcsRunner`.
- [StatusReader.cs](../../Infrastructure/StatusReader.cs) - use `UvcsRunner`.
- [StatusCommand.cs](../../Commands/StatusCommand.cs) - use the injected reader/runner path.
- [PendingChangesDiffsCommand.cs](../../Commands/PendingChangesDiffsCommand.cs) - route `cm fileinfo` and `cm getfile` through `UvcsRunner`; keep Git/fc process handling unchanged.
- [ChangesetDiffsCommand.cs](../../Commands/ChangesetDiffsCommand.cs) - direct `cm diff` metadata parsing, logical change mapping, batch construction, batch download, complete-output validation, and ordered batch pipeline.
- [NV-AITools.csproj](../../NV-AITools.csproj) - version 1.3.0.
- [BuildRelease.bat](../../Packaging/BuildRelease.bat) - version 1.3 defaults and accurate batching/concurrency README text.
- [Install.bat](../../Packaging/Install.bat) - run the packaged dependency preflight before any installation mutation and surface its error clearly.
- [UVCS_Tools_Implementation_Outline.md](../UVCS_Tools_Implementation_Outline.md) and [UVCS_Tools_Release_Steps.md](../UVCS_Tools_Release_Steps.md) - replace the obsolete 16-Plastic-process contract and acceptance checks.
- [AI_Agnostic_Broker_Working_Design.md](./AI_Agnostic_Broker_Working_Design.md) - retain the old version 1.1 section as history and append the implemented 1.3 correction when implementation is complete.

### Tests

- [NV-AITools.Tests/Program.cs](../../../NV-AITools.Tests/Program.cs) - retain the existing pending-pipeline tests and add focused process-gate, metadata-parser, batch-builder, batch-pipeline, and failure tests.

## 12. Required automated tests

### UVCS runner

1. At most one wrapped fake process is active across several simulated workspace callers.
2. Cancellation while waiting starts no process.
3. Cancellation while active terminates and releases the gate.
4. A failure releases the gate.
5. Minimum, newer, older, malformed, and missing version outputs produce the correct result.
6. The internal dependency command returns success or dependency failure without publishing a queue request.

Use an injectable executable name or narrow process delegate in tests; do not invoke real authenticated `cm` in the repository suite.

### Metadata parser

1. Changed, added, deleted, and moved rows map to the expected logical items.
2. A move becomes delete plus add.
3. Pure moves cannot disappear.
4. Moves into and out of supported extensions filter each side independently.
5. Case-only move paths remain separate and deterministically ordered.
6. Files, binaries, symlinks, directories, Xlinks, and unsupported extensions follow the declared type/filter policy.
7. Malformed fields, unsafe paths, missing move paths, unknown statuses/types, and conflicting duplicates fail.
8. Equal endpoints with empty UVCS output return an empty patch.

### Batch builder and downloader

1. No batch exceeds 16 revision arguments.
2. A two-sided logical item is never split.
3. Mixed changed/added/deleted items pack deterministically.
4. Command-length pressure flushes before overflow.
5. Semicolon and oversized arguments select the single-download fallback.
6. Every argument is added through `ArgumentList` as one value.
7. Nonzero batch exit returns child diagnostics and publishes nothing.
8. Exit zero with one or several missing files lists exact endpoints and publishes nothing.
9. Complete batch output publishes all pairs once.

### Ordered batch pipeline

1. Exactly one batch producer runs.
2. Transform concurrency reaches but never exceeds 16.
3. Comparison overlaps later batch preparation.
4. The channel applies backpressure at 16 prepared items.
5. Results return in source order despite out-of-order completion.
6. Producer failure, consumer failure, and external cancellation terminate without hangs or leaked workers.
7. No item from an incomplete batch is transformed.
8. Duplicate, missing, and out-of-range result indices fail rather than returning default results.

### Regression fixtures

1. Per-file patch output remains equivalent for changed, added, and deleted files.
2. A file move remains delete/add while rename detection is disabled; a directory move fails explicitly.
3. Binary-like files remain skipped.
4. Algorithms and exit codes remain unchanged.
5. Pending filters and per-file failure counts remain unchanged under serialized UVCS calls.
6. Queue concurrency, crash recovery, shutdown, and hidden broker startup tests still pass.

## 13. Receiving-machine acceptance gate

Do not package version 1.3 until all of these pass through the installed client and authenticated broker:

1. `cm version` is accepted and logged; an intentionally raised minimum produces a clear pre-install failure with no installation changes and a clear broker startup failure in a test build.
2. A collection `cm getfile` call with `--raw` downloads changed, added, and deleted endpoint content byte-for-byte.
3. Changesets 21366 to 21380 complete successfully at least five consecutive times.
4. Every successful repetition produces the same patch hash.
5. A range with at least 32 eligible files shows one active `cm` process maximum and active local comparisons overlapping later downloads.
6. Two AI clients submit changeset requests in two configured workspaces; both finish, the UVCS process peak remains one, and local comparison overlap is observed.
7. Pending changes with several controlled files still produce the expected per-file report while the UVCS process peak remains one.
8. Add, delete, modify, pure file move, move-and-modify, case-only file move, moved directory failure, binary-like source, spaces, Unicode, and a semicolon path are exercised.
9. Broker stop during a batch kills the process, publishes the stopped result, and leaves no queue or temp residue.
10. Install, upgrade, login startup, configuration reload, uninstall, release ZIP inspection, and `git diff --check` pass.

Record timing for the old sequential implementation, failed 16-process implementation, and new one-process batched implementation. Performance is informative; correctness and deterministic output are the release gates.

## 14. Implementation sequence

1. Add `UvcsRunner` with version validation and fake-process tests.
2. Add the internal dependency-check command and installer preflight before any installation mutation.
3. Inject the shared runner through broker startup, workspace validation, status, pending, and changeset call paths.
4. Confirm all existing tests pass with the one-process invariant before changing changeset discovery.
5. Add and test the direct `cm diff` metadata parser and logical move expansion.
6. Add and test the deterministic batch builder and single-item fallback.
7. Add and test `OrderedBatchPipeline`.
8. Convert changeset preparation to sequential multi-revision batches and retain the current Git transform.
9. Add exact batch/missing-output diagnostics.
10. Run the repository suite and inspect the full diff.
11. Perform the authenticated receiving-machine acceptance gate.
12. Update version, packaging, generated README text, skill guidance if wording changed, implementation outline, release steps, and the overall design history.
13. Build and inspect the 1.3.0 release only after acceptance succeeds.

## 15. Rejected initial alternatives

### Four concurrent batched `cm` processes

Potentially faster, but it retains the unproven shared-authentication concurrency assumption that caused version 1.2 to fail. Keep one as the baseline. Consider two, then four, only as a later internal constant change with authenticated stress evidence.

### `cm diff --download`

It is the shortest command, but Unity only documents that it stores differences content. The current tool needs explicit old/new identity, deterministic paths, complete-pair publication, binary checks, and exact failure attribution. Adopting undocumented layout behavior would exchange visible code for an implicit contract.

### Automatic retry and recursive batch splitting

Useful for a continue-on-error bulk service, but unnecessary for a fail-fast, read-only changeset command once exact valid revisions are batched through one process. It complicates cancellation, diagnostics, process counts, and partial-file cleanup. Add only if real one-process batch failures demonstrate a need.

### Capture file contents from stdout

The current `ProcessRunner` is text-based, while revision content must remain byte-exact and may be binary-like until inspected. Destination files are the correct transport.

### Revert to fully sequential export and post-export comparison

Reliable but leaves UVCS's supported batching unused and loses overlap between downloads and local comparisons. The one-process batch producer retains the same safety with fewer client startups and a rolling transform stage.

## Completion Criteria

Version 1.3 is complete only when:

- the broker enforces one active `cm` process globally,
- changeset candidates come from direct endpoint metadata,
- changeset content uses supported multi-revision batches,
- complete pairs flow into up to 16 local comparisons without waiting for all downloads,
- no partial batch can produce a partial patch,
- file moves retain delete/add semantics and directory moves fail explicitly,
- older UVCS clients fail before broker readiness,
- all automated tests pass, and
- the authenticated receiving-machine gate passes repeatedly.
