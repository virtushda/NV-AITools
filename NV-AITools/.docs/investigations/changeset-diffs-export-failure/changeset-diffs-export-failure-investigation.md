# Investigation: Changeset Diffs Export Failure

## Scope

Investigate why `RunChangesetDiffs.bat` fails for changesets 21366 through 21380 when exporting source files, determine the complete root cause, and identify the smallest reliable correction without changing production code.

## Intended Behavior

The wrapper should submit a fixed `changeset-diffs` request through the installed NV-AITools client. The authenticated broker should discover every changed source path, export the file state at both requested endpoints when present, compare completed endpoint pairs, and return one deterministic patch.

## Areas Investigated

- [x] Wrapper and CLI argument flow - confirmed the endpoints, workspace, stdout redirection, stderr relay, and public options.
- [x] Broker request and diagnostics flow - traced the visible error to the command response and explained why `broker.log` has only a summary.
- [x] Changeset candidate discovery - traced the `cm log` query, status parsing, path normalization, extension filtering, deduplication, and sorting.
- [x] Endpoint existence model - traced success, absent-revision, and anomalous-success paths and exercised an added file successfully.
- [x] Plastic export command - verified command construction, documented selector syntax, output option, exit handling, and missing-revision handling.
- [x] Temporary path and filesystem checks - verified unique roots, containment, parent creation, ownership, and cleanup timing.
- [x] Parallel pipeline ownership - checked destinations, producer/consumer handoff, cancellation, comparison timing, process state, and shared budgets.
- [x] Regression analysis - compared the current 16-producer implementation with the prior sequential implementation.
- [x] Exact runtime reproduction - repeatedly reproduced through the installed 1.2 client and authenticated broker without invoking `cm` directly.
- [x] Existing tests and validation gaps - ran all repository tests and compared them with the documented receiving-machine acceptance gate.

## Confirmed Issues

### H1 - Changeset exports assume unsupported authenticated `cm cat` process concurrency

Severity: High

The current command starts up to 16 preparation workers. Each worker runs a unique old-endpoint `cm cat` and then a unique new-endpoint `cm cat`, so a single request can have 16 independent `cm` processes writing different files at once. In the configured authenticated environment, larger candidate sets repeatedly produce `cm` exit code 0 without the requested destination file. The command correctly refuses to construct a partial patch, but the feature is therefore unusable for the reported range.

This is confirmed as a system-level concurrency incompatibility in the current design. The exact internal Unity Version Control failure mechanism remains unobservable from NV-AITools because `cm` is outside the sandbox and the anomalous-success branch discards its captured output. It is not necessary to know that private mechanism to establish the NV-AITools defect: the application shipped an assumption its own design document marked as requiring authenticated stress validation, and the supported installed workflow consistently violates that assumption.

Evidence:

- [ChangesetDiffsCommand.ExecuteAsync](../../../Commands/ChangesetDiffsCommand.cs#L24) feeds every candidate into the shared ordered pipeline.
- [OrderedParallelPipeline.MaximumConcurrency](../../../Infrastructure/OrderedParallelPipeline.cs#L8) creates up to 16 preparation workers.
- [ChangesetDiffsCommand.PrepareAsync](../../../Commands/ChangesetDiffsCommand.cs#L92) keeps the two endpoints sequential for one path but permits different paths to export concurrently.
- [ChangesetDiffsCommand.TryExportAsync](../../../Commands/ChangesetDiffsCommand.cs#L258) launches `cm cat serverpath:/...#cs:... --file=... --raw` and throws the reported message only when the child returned 0 but its unique output file is absent.
- [ProcessRunner.RunAsync](../../../Infrastructure/ProcessRunner.cs#L47) creates an independent `ProcessStartInfo` and `Process` for every call and waits for process exit plus both redirected streams before returning.
- The earlier implementation exported candidates in a single sequential loop. Commit comparison `a7cbae8..cc89ed4` shows the concurrency change and no corresponding selector or destination-contract change.
- The implemented design explicitly requires up to 16 simultaneous Plastic processes in [the changeset pipeline](../../plan/AI_Agnostic_Broker_Working_Design.md#205-changeset-pipeline) but warns in [the release decision](../../plan/AI_Agnostic_Broker_Working_Design.md#2010-release-decision-and-residual-risks) not to package it before authenticated concurrency validation because public `cm` documentation does not guarantee that shared-profile behavior.
- The repository release checklist likewise requires at least 32 eligible files and confirmation of the process peak in [UVCS_Tools_Release_Steps.md](../../UVCS_Tools_Release_Steps.md#L39).
- Unity documents `cat`/`getfile`, `--file`, `--raw`, and `serverpath` revision specs, but does not document parallel independent-client safety: [GETFILE](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/getfile) and [OBJECTSPEC](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/objectspec).

Runtime matrix through the installed 1.2 client and external broker:

| Range | Repetitions | Result |
|---|---:|---|
| 21380 to 21380 | 3 | 3 successes; one candidate (`EmbryoGroup.cs`) |
| 21366 to 21368 | 5 directly observed | 5 successes; three candidates, including an added file with an intentionally absent old endpoint |
| 21366 to 21366 | 3 | 3 failures; first report moved among `ModularFlattening.cs`, `TextureCreator.cs`, and `SpawnAnimalGroupAction.cs` |
| 21378 to 21380 | 4 | 4 failures; first report moved among `TerrainHeightNative.cs`, `UnsafeSpatialHashGrid.cs`, `TrackedCollectionManager.cs`, and `EmbryoGroup.cs` |
| 21366 to 21380 | user plus 6 investigation runs | 7 failures; at least five different paths appeared first |
| 21225 to 21380 | 2 | 2 failures; different first paths |

The first failed filename moving between identical requests rules out one malformed candidate as the command-level cause. The reliable three-candidate range, which correctly handles an added file, also rules out the normal missing-revision branch as the reported failure mode.

A previously produced 21225-to-21380 patch contains 152 file patches and valid old/new sides for the paths that now fail, including [TextureCreator.cs](file:///X:/UnityProjects/PK-PSCM-2021/Review/Reviews/plastic-log-diff-21225-to-21380.patch#L448), [SpawnAnimalGroupAction.cs](file:///X:/UnityProjects/PK-PSCM-2021/Review/Reviews/plastic-log-diff-21225-to-21380.patch#L1620), [TerraformAction.cs](file:///X:/UnityProjects/PK-PSCM-2021/Review/Reviews/plastic-log-diff-21225-to-21380.patch#L8265), and [UnsafeSpatialHashGrid.cs](file:///X:/UnityProjects/PK-PSCM-2021/Review/Reviews/plastic-log-diff-21225-to-21380.patch#L10311). This further excludes invalid paths and permanently unavailable historical content.

### M1 - The anomalous-success branch discards the only child-process diagnostics

Severity: Medium

[ChangesetDiffsCommand.TryExportAsync](../../../Commands/ChangesetDiffsCommand.cs#L277) checks the destination immediately after an exit code of 0, but its missing-file exception does not include the revision, destination, `StandardError`, or `StandardOutput`. Both streams have already been captured by [ProcessRunner.RunAsync](../../../Infrastructure/ProcessRunner.cs#L100). Consequently, any warning emitted by `cm` during this anomalous success is lost, and the client can identify neither the failing endpoint nor the actual `cm` response.

The absence of details from `broker.log` is not itself a defect. [WorkspaceRuntime.ExecuteAsync](../../../Broker/WorkspaceRuntime.cs#L157) intentionally logs request identity, timing, and final exit code, while [CommandDispatcher.ExecuteAsync](../../../Broker/CommandDispatcher.cs#L9) publishes command diagnostics in the request's `.stderr` result. The wrapper leaves stderr visible and redirects only stdout in [RunChangesetDiffs.bat](../../../Review/RunChangesetDiffs.bat#L28).

## Ruled Out / Corrected

- **Wrapper argument corruption:** ruled out. The wrapper passes the two prompted values and its parent workspace to the installed executable without transformation.
- **Broker not running or workspace not configured:** ruled out. Every request was claimed and completed; the installed client reached command execution and received a normal exit-code-5 result.
- **One invalid or deleted path:** ruled out. Identical requests fail first on different ordinary source paths, current files exist, and an older endpoint patch contains valid old/new file sides.
- **Normal absent-revision behavior:** ruled out as the reported condition. The successful three-candidate range includes a new file, proving this client returns the expected nonzero/missing-revision diagnostic and the command represents the absent side as `/dev/null` in that case.
- **Revision-selector syntax:** ruled out. The syntax matches Unity's documented `serverpath` plus changeset revision specification and works consistently for small candidate sets.
- **Destination collision:** ruled out. Every request owns a GUID-named temporary root; each candidate owns its normalized repository path under separate `a` and `b` endpoint trees.
- **Unsafe or escaping repository path:** ruled out. Normalization rejects traversal and NULs; endpoint containment is rechecked before use.
- **Comparison beginning too early:** ruled out. A pair is published only after both endpoint calls complete, and only its consumer reads or deletes those two unique files.
- **Shared `Process` state:** ruled out. Each child call owns a new process and immutable argument list; `ProcessRunner` has no mutable instance fields.
- **Process completion race inside NV-AITools:** ruled out. The runner waits for child exit and both redirected streams before `File.Exists` is evaluated.
- **Output-budget or timeout failure:** ruled out. Those paths throw explicit limit/timeout exceptions and do not return the observed exit-0/missing-file branch.
- **Pipeline ordering corruption:** ruled out by source trace and repository tests; results are indexed and assembled by the coordinator.

## Regression Analysis

The old changeset command exported every candidate and both endpoints sequentially before running one Git tree comparison. Version 1.1/1.2 changed preparation to up to 16 workers and comparison to up to 16 workers. The current command retained the same `cm cat` selector and per-file destination semantics; the material new precondition is concurrent authenticated `cm` client execution.

The production unit/integration suite passes in full, including scheduler saturation, backpressure, cancellation, ordered output, Git equivalence, queue concurrency, recovery, and shutdown. It does not execute an authenticated Plastic export. This matches the documented validation split: the real Plastic client/profile test was deliberately left as a receiving-machine acceptance step and was not completed before packaging.

## Recommended Correction

### Immediate reliable correction

Serialize the changeset command's Plastic export preparation while retaining up to 16 local Git comparison workers and the bounded rolling handoff. A single preparation worker can export one complete pair and publish it immediately; local comparisons can still overlap later exports. This removes the only demonstrated unsafe assumption without reverting the deterministic per-file comparison work.

The serialization boundary should cover both endpoint exports for one candidate. If reliability must hold across multiple simultaneously active workspaces, the gate must be broker-wide for changeset `cm cat` operations rather than merely local to one command instance. The current broker permits four workspace lanes, so a per-command gate alone still allows four concurrent `cm cat` processes.

Do not use an unexplained sleep or treat exit code 0 plus no file as an absent revision. Either would hide data loss. A retry may be evaluated later as an optimization, but it should not be the first correctness fix without proving that the retried file contents and endpoint identity are exact.

### Diagnostic correction

When exit code 0 produces no destination, include the endpoint changeset/revision and non-empty captured child diagnostics in the returned error. Continue to keep the broker operational log concise; the request stderr channel is the correct place for command detail.

### Follow-up optimization to evaluate separately

Unity Version Control 11.0.16.8411 added collection arguments to `getfile`/`cat`, where one CLI process accepts multiple `revspec;destination` pairs. This may be both simpler and faster than 16 simultaneously authenticated client processes: [official 11.0.16.8411 release notes](https://www.plasticscm.com/download/releasenotes/11.0.16.8411). Before adopting it, establish a minimum supported client version and validate additions, deletions, moves, partial batch failure attribution, argument-size limits, cancellation, and exact output parity. It is not the minimal hotfix.

## Validation Required For The Fix

1. Add an integration seam or fake process runner proving changeset preparation never has more than one active Plastic export while comparison still reaches its intended concurrency and overlaps exports.
2. Preserve deterministic candidate-order output and fail-fast cancellation.
3. On the receiving machine, run 21366-to-21380 repeatedly and verify zero missing destinations and stable patches.
4. Run a range with at least 32 eligible files and at least one addition and deletion.
5. Run two configured workspaces concurrently to validate the chosen broker-wide or per-command serialization scope.
6. Confirm the anomalous-success diagnostic includes the exact endpoint and any non-empty `cm` output.
7. Re-run the repository suite and package smoke checks.

## Test Evidence

Command: `dotnet run --project NV-AITools.Tests\\NV-AITools.Tests.csproj`

Result: all 13 test groups passed. This verifies the in-process pipeline and broker mechanics but does not alter H1 because no test authenticates to Plastic or runs `cm cat`.
