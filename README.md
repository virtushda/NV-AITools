# NV-AITools

NV-AITools 1.2 exposes authenticated, read-only Unity Version Control status and diff operations to sandboxed AI agents through a per-user tray broker and durable workspace queues.

Pending and changeset diffs use ordered two-stage pipelines with up to 16 concurrent Plastic operations and 16 concurrent local comparisons per active command. Top-level requests remain serial within each workspace. Rename detection is intentionally disabled because independent per-file patches cannot detect cross-file renames correctly.

Pending-change diffs can be limited with repeatable, case-insensitive `--file-filter <glob>` options. Filters match filename basenames in any directory, support `*` and `?`, and combine with OR.

See [the implementation design](./NV-AITools/.docs/plan/AI_Agnostic_Broker_Working_Design.md) and [release steps](./NV-AITools/.docs/UVCS_Tools_Release_Steps.md).
