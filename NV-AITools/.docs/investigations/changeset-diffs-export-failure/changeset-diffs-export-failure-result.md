# Changeset Diffs Export Failure - Investigation Result

## Verdict

`RunChangesetDiffs.bat` reaches the broker correctly. The failure is inside the version 1.1/1.2 changeset preparation stage: NV-AITools starts up to 16 authenticated `cm cat --file` processes concurrently, and the installed Unity Version Control client sometimes returns exit code 0 without creating its requested output file under that load.

This is a confirmed NV-AITools design defect even though the private internal reason inside `cm` is not visible. The application assumes a concurrency contract that Unity does not document, the project plan explicitly required receiving-machine validation before release, and the real authenticated workflow consistently violates that contract.

No production code was changed during this investigation.

## Confirmed Problems

### H1 - Concurrent changeset exports are unreliable

Severity: High

[ChangesetDiffsCommand.ExecuteAsync](../../../Commands/ChangesetDiffsCommand.cs#L24) sends candidates into [OrderedParallelPipeline.RunAsync](../../../Infrastructure/OrderedParallelPipeline.cs#L10), whose preparation stage reaches 16 workers. Each worker runs the old and new exports sequentially for its own file, but different files concurrently launch independent `cm cat` processes from [ChangesetDiffsCommand.TryExportAsync](../../../Commands/ChangesetDiffsCommand.cs#L258).

The exact reported branch requires all of the following:

1. `cm cat` has exited.
2. Its exit code is 0.
3. Its unique requested destination does not exist.

[ProcessRunner.RunAsync](../../../Infrastructure/ProcessRunner.cs#L47) waits for process exit and both redirected streams. Temporary roots are GUID-unique, endpoint roots are separate, repository paths are contained, and no consumer receives a pair until both exports finish. There is no NV-AITools destination collision or early cleanup.

Runtime evidence through the installed 1.2 client and authenticated external broker:

| Candidate load | Observed result |
|---|---|
| One candidate, 21380 to 21380 | 3 of 3 succeeded |
| Three candidates, 21366 to 21368 | 5 directly observed runs succeeded; an added file's missing old endpoint was handled correctly |
| Larger ranges | 16 of 16 observed requests failed across 21366-only, 21378-to-21380, 21366-to-21380, and 21225-to-21380 |

The first failed filename changes between identical requests. Reported first failures included ordinary existing files such as `TextureCreator.cs`, `SpawnAnimalGroupAction.cs`, `TerraformAction.cs`, `TrackedCollectionManager.cs`, and `UnsafeSpatialHashGrid.cs`. A prior 152-file patch contains valid old/new revisions for the same paths, including [TextureCreator.cs](file:///X:/UnityProjects/PK-PSCM-2021/Review/Reviews/plastic-log-diff-21225-to-21380.patch#L448) and [UnsafeSpatialHashGrid.cs](file:///X:/UnityProjects/PK-PSCM-2021/Review/Reviews/plastic-log-diff-21225-to-21380.patch#L10311).

The regression is the preparation concurrency. The earlier command exported candidates sequentially. The current project design calls for 16 simultaneous Plastic processes in [the changeset pipeline](../../plan/AI_Agnostic_Broker_Working_Design.md#205-changeset-pipeline), then explicitly says in [the release decision](../../plan/AI_Agnostic_Broker_Working_Design.md#2010-release-decision-and-residual-risks) not to package it without authenticated concurrency validation. That test was not represented in the repository suite.

Unity's public command documentation supports the selector and output syntax used here: [`cat`/`getfile --file --raw`](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/getfile) and [`serverpath` changeset revision specs](https://docs.unity.com/en-us/unity-version-control/uvcs-cli/objectspec). It does not promise that many separate clients sharing one authentication profile can safely perform these downloads concurrently.

### M1 - The decisive `cm` diagnostics are discarded

Severity: Medium

On exit code 0 plus a missing destination, [ChangesetDiffsCommand.TryExportAsync](../../../Commands/ChangesetDiffsCommand.cs#L277) throws a generic message without the endpoint changeset, destination, `StandardError`, or `StandardOutput`. Those streams were captured, but this branch drops them. This prevents the response from revealing whether `cm` emitted a warning during its anomalous success.

The sparse `broker.log` is intentional. [WorkspaceRuntime.ExecuteAsync](../../../Broker/WorkspaceRuntime.cs#L157) records request lifecycle, timing, and exit code; detailed command diagnostics are returned through the request stderr file by [CommandDispatcher.ExecuteAsync](../../../Broker/CommandDispatcher.cs#L9). [RunChangesetDiffs.bat](../../../Review/RunChangesetDiffs.bat#L28) redirects only patch stdout, leaving stderr visible.

## Minimum Reliable Correction

Serialize changeset Plastic preparation and retain the rolling parallel comparison stage:

1. Permit only one changeset export pair at a time.
2. Export that candidate's old and new endpoints sequentially.
3. Publish the completed pair immediately to the bounded channel.
4. Keep up to 16 local Git comparison workers consuming completed pairs.
5. Assemble results in candidate order as today.

This preserves overlap between Plastic I/O and local diff work while removing the demonstrated unsafe behavior. To provide the promised reliability across multiple configured workspaces, make the `cm cat` serialization boundary broker-wide. A gate scoped only to one command still permits up to four concurrent changeset exports because the broker has four workspace lanes.

Do not interpret exit code 0 plus no file as an absent revision, and do not add a timing sleep. Both can silently turn an export failure into an incorrect patch. A retry can be investigated after correctness is restored, but it is not a substitute for a proven concurrency contract.

Improve the anomalous-success error at the same time: identify the old/new endpoint and changeset and append any non-empty captured child diagnostics to request stderr.

## Optional Follow-up Optimization

Unity Version Control 11.0.16.8411 added multi-revision `getfile`/`cat` arguments: one process can accept multiple `revspec;destination` pairs. This is a promising replacement for many simultaneous authenticated processes and may recover most of the desired throughput. See the [official 11.0.16.8411 release notes](https://www.plasticscm.com/download/releasenotes/11.0.16.8411).

Treat it as a separate upgrade, not the hotfix. It requires a declared minimum client version and authenticated tests for additions, deletions, moves, partial batch failures, argument limits, cancellation, and patch parity.

## Completed Investigation Areas

- Wrapper and CLI arguments
- Broker queue, response, and logging flow
- Candidate discovery and endpoint-existence semantics
- Plastic command and revision selector construction
- Temporary-path containment and lifetime
- Producer/consumer ownership, cancellation, and output ordering
- Process creation, completion, and output budgets
- Sequential-to-parallel regression history
- Repeated authenticated runtime reproduction
- Repository tests and release-validation gap

## Validation After Correction

1. Prove at the integration seam that only one broker-wide changeset `cm cat` export is active while local comparisons still overlap and can reach their configured limit.
2. Repeat 21366-to-21380 several times and compare stable output hashes.
3. Exercise at least 32 eligible files including an addition and deletion.
4. Run changeset requests in two configured workspaces simultaneously.
5. Verify an injected exit-0/missing-file result reports the exact endpoint plus captured child diagnostics.
6. Re-run the repository tests and release smoke checks.

## Test Result

`dotnet run --project NV-AITools.Tests\\NV-AITools.Tests.csproj` passed all 13 test groups. This confirms the scheduler, queue, Git-equivalence, cancellation, recovery, and shutdown tests still pass; none authenticates to Plastic or covers concurrent `cm cat` behavior.
