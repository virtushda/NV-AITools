---
name: use-uvcs-tools
description: Use the installed read-only UvcsTools executable to inspect Unity Version Control workspace status, generate pending source-file differences, or compare source files between changesets. Trigger for UVCS or Plastic SCM status and diff requests where Codex must verify the workspace review wrappers and use the approved read-only tool instead of invoking cm.exe, Git, batch wrappers, or shell-built commands directly.
---

# Use UVCS Tools

Invoke only the installed executable at `C:\Program Files\UvcsTools\UvcsTools.exe`. Do not invoke `cm.exe` directly. Do not use the human-facing batch wrappers.

## Verify workspace setup

Before invoking the executable, determine the workspace root from the user's requested workspace or the active workspace context. Verify that all of these human-facing wrapper files exist:

- `Review\RunUvcsStatus.bat`
- `Review\RunPendingChangesDiffs.bat`
- `Review\RunChangesetDiffs.bat`

Treat the wrappers as workspace setup sentinels; never execute them as an agent. If any wrapper is missing, do not invoke UvcsTools. Emit this clear error to the user, listing each missing path:

```text
UVCS Tools workspace setup is incomplete. Missing required wrapper file(s):
- <missing workspace-relative path>

Copy the Review folder from the UvcsTools workspace kit into the workspace root, then retry. No UVCS command was run.
```

## Select a command

- Read structured workspace status:

  ```powershell
  & "C:\Program Files\UvcsTools\UvcsTools.exe" status --workspace "X:\path\inside\workspace"
  ```

- Generate numbered pending-change differences for supported Unity source files:

  ```powershell
  & "C:\Program Files\UvcsTools\UvcsTools.exe" pending-changes-diffs --workspace "X:\path\inside\workspace"
  ```

- Generate a unified patch between changeset snapshots (Generally don't use this unless explicitly directed to by the user):

  ```powershell
  & "C:\Program Files\UvcsTools\UvcsTools.exe" changeset-diffs --from 20640 --to 20663 --workspace "X:\path\inside\workspace"
  ```

Add `--algorithm histogram|patience|default|minimal` or `--find-renames` only when the user requests it. Histogram is the default.

## Handle the result

Treat stdout as the complete result payload. Treat stderr as progress and diagnostics. Check the process exit code before using stdout:

- `0`: success, including an empty valid changeset patch.
- `2`: invalid arguments; correct the invocation without broadening it.
- `3`: missing dependency or invalid workspace; report the environment problem.
- `4`: UVCS, Git, or comparison process failure; report stderr.
- `5`: parsing, temporary-file, or internal processing failure; report stderr.

Pass only the documented options. Never add arbitrary UVCS, Git, PowerShell, command-shell, or output-file passthrough arguments. Do not redirect the result to a project file unless the user explicitly asks for a saved artifact.
