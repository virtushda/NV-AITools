# NV-AITools

NV-AITools 1.3 exposes authenticated, read-only Unity Version Control status and diff operations to sandboxed AI agents through a per-user tray broker and durable workspace queues.

The broker permits one authenticated UVCS process at a time across all workspaces. Changeset diffs use sequential downloads of up to 16 revisions per process and overlap completed batches with up to 16 local Git comparisons. Pending diffs retain up to 16 preparation and local comparison workers, while all of their UVCS calls use the same broker-wide gate. Top-level requests remain serial within each workspace. Rename detection is intentionally disabled because independent per-file patches cannot detect cross-file renames correctly.

File moves are emitted as delete/add patches. A moved directory fails explicitly instead of silently returning an incomplete patch for its descendants.

Pending-change diffs can be limited with repeatable, case-insensitive `--file-filter <glob>` options. Filters match filename basenames in any directory, support `*` and `?`, and combine with OR.

See [the implementation design](./NV-AITools/.docs/plan/AI_Agnostic_Broker_Working_Design.md) and [release steps](./NV-AITools/.docs/UVCS_Tools_Release_Steps.md).
