---
name: use-nv-ai-tools
description: Use the installed read-only NV-AITools executable to inspect Unity Version Control workspace status, generate pending source-file differences, or compare source files between changesets. Trigger for UVCS or Plastic SCM status and diff requests where Codex must use the approved broker client instead of invoking cm.exe, Git, batch wrappers, or shell-built commands directly.
---

# Use NV-AITools

Invoke only the installed executable at `%LOCALAPPDATA%\Programs\NV-AITools\NV-AITools.exe`. Do not invoke `cm.exe` or `git.exe` directly. Do not use the human-facing batch wrappers.

## Security assumption

NV-AITools is installed in a directory writable by the current Windows user. This workflow assumes Codex runs in a sandbox that prevents writes outside approved workspaces, including the installed executable and skill directories. Without that boundary, an unrestricted AI can replace the executable and run arbitrary code through this skill; using the workflow without an appropriate sandbox means accepting that risk.

The installed executable is a queue client. It discovers the nearest configured workspace from the supplied path and sends a fixed request to the authenticated NV-AITools tray broker. The broker must already be running, and the workspace root must be listed in its adjacent `config.ini`.

## Select a command

- Read structured workspace status:

  ```powershell
  & "$env:LOCALAPPDATA\Programs\NV-AITools\NV-AITools.exe" status --workspace "X:\path\inside\workspace"
  ```

- Generate numbered pending-change differences for supported Unity source files:

  ```powershell
  & "$env:LOCALAPPDATA\Programs\NV-AITools\NV-AITools.exe" pending-changes-diffs --workspace "X:\path\inside\workspace"
  ```

  When the user requests specific filenames or the task clearly limits the diff scope, add one or more filename filters:

  ```powershell
  & "$env:LOCALAPPDATA\Programs\NV-AITools\NV-AITools.exe" pending-changes-diffs --workspace "X:\path\inside\workspace" --file-filter "Animal*.cs" --file-filter "Shader??.compute"
  ```

  Filters are case-insensitive, match only the filename basename in any folder, support `*` and `?`, and combine with OR. Do not include directory separators. Omit all filters when a complete pending-change report is required.

- Generate a unified patch between changeset snapshots (Generally don't use this unless explicitly directed to by the user):

  ```powershell
  & "$env:LOCALAPPDATA\Programs\NV-AITools\NV-AITools.exe" changeset-diffs --from 20640 --to 20663 --workspace "X:\path\inside\workspace"
  ```

Add `--algorithm histogram|patience|default|minimal` only when the user requests it. Histogram is the default. Rename detection is not supported by the parallel per-file changeset flow.

## Handle the result

Treat stdout as the complete result payload. Treat stderr as progress and diagnostics. Check the process exit code before using stdout:

- `0`: success, including an empty valid changeset patch.
- `2`: invalid arguments; correct the invocation without broadening it.
- `3`: broker unavailable, workspace not configured, missing dependency, or invalid workspace; report the environment problem. Do not start the broker or request sandbox escalation.
- `4`: UVCS, Git, or comparison process failure; report stderr.
- `5`: parsing, temporary-file, or internal processing failure; report stderr.

Pass only the documented options. Never add arbitrary UVCS, Git, PowerShell, command-shell, or output-file passthrough arguments. Never request permission to invoke the tool outside the sandbox; report broker/setup failures instead. Do not redirect the result to a project file unless the user explicitly asks for a saved artifact.
