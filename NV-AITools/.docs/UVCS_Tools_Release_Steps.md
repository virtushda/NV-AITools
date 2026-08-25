# NV-AITools Release Steps

## Prerequisites

1. Install the .NET 10 SDK on the computer that builds the release.
2. Ensure the built-in Windows `tar.exe` command is available.
3. Keep the application version in `major.minor.patch` form using non-negative integers, such as `1.3.0`.

The receiving computer does not need the .NET runtime because the application is published as a self-contained executable.

## Build

Run [BuildRelease.bat](../Packaging/BuildRelease.bat) from a normal command prompt:

```bat
Packaging\BuildRelease.bat 1.3.0
```

The script restores the project, publishes a self-contained single-file Windows x64 executable with its tray icon embedded, generates `README.txt`, stages the hidden startup script, `config.ini`, the skill, and optional workspace wrappers, and creates one portable ZIP:

```text
Artifacts\NV-AITools-1.3.0-win-x64.zip
```

## Verify

1. Extract the generated ZIP outside the repository.
2. Confirm the package contains `NV-AITools.exe`, `StartBroker.vbs`, `config.ini`, `Install.bat`, `Uninstall.bat`, `README.txt`, `SkillTemplate`, and `WorkspaceKit`.
3. On a Windows account with Unity Version Control 11.0.16.8411 or newer and Git installed, run `Install.bat` without elevation. Confirm an older client is rejected before the old broker is stopped or any installed files are changed.
4. Confirm the executable exists under `%LOCALAPPDATA%\Programs\NV-AITools`.
5. Confirm `config.ini` and `broker.log` exist beside the executable and the NV-AITools tray icon is visible.
6. Confirm the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `NV-AITools` invokes the installed `StartBroker.vbs` through `wscript.exe //B //NoLogo`.
7. Confirm the skill exists under `%USERPROFILE%\.agents\skills\use-nv-ai-tools`.
8. Add at least one absolute UVCS workspace root to the installed `config.ini`, add `.nv-ai-tools/` to that workspace's ignore rules, and select `Reload Configuration` from the tray menu.
9. Confirm `.nv-ai-tools\requests`, `.nv-ai-tools\processing`, and `.nv-ai-tools\results` were created under the configured workspace.
10. Invoke all three executable commands from a descendant directory and verify stdout, stderr, and exit codes. Confirm the AI-side invocation does not require direct Plastic authentication or sandbox escalation.
11. Run `pending-changes-diffs` with an exact mixed-case `--file-filter`, wildcard `*` and `?` filters, and two repeated filters. Confirm matching is case-insensitive, uses basenames in any folder, combines filters with OR, and excludes unmatched files before per-file Plastic work. Confirm a valid unmatched filter succeeds with zero processed files and a filter containing `/` or `\` fails with exit code `2`.
12. Submit at least two simultaneous requests in one workspace and requests across at least five configured workspaces. Confirm one-at-a-time top-level execution per workspace and no more than four simultaneous cross-workspace operations.
13. Run pending and changeset comparisons with at least 32 eligible text files. Confirm the broker never exceeds one active UVCS process across all workspaces. Confirm changeset downloads contain at most 16 revision entries per process, local comparison reaches but never exceeds 16 workers, comparison begins before all batches finish, and repeated output is deterministic.
14. Confirm `--find-renames` and a crafted protocol request with `findRenames=true` both fail with exit code `2`.
15. Save an invalid candidate `config.ini`, reload, and confirm the existing watchers continue to serve requests while the tray reports the error.
16. Optionally copy the packaged `WorkspaceKit\Review` folder into a real UVCS workspace root and confirm these files exist:
   - [RunUvcsStatus.bat](../Review/RunUvcsStatus.bat)
   - [RunPendingChangesDiffs.bat](../Review/RunPendingChangesDiffs.bat)
   - [RunChangesetDiffs.bat](../Review/RunChangesetDiffs.bat)
17. Run `Install.bat` again with a recognizable configuration edit already present and an obsolete `use-uvcs-tools` skill directory in place. Confirm the running broker is replaced, the edit is preserved, and the obsolete skill is removed.
18. Run `Uninstall.bat` and verify that the broker stops; the automatic-start entry, installed application directory, current skill, and obsolete skill are removed; and workspace Review and `.nv-ai-tools` folders remain untouched.
19. Run changesets 21366 through 21380 at least five consecutive times through the installed authenticated broker. Confirm every run succeeds and produces the same patch hash.
20. Exercise added, deleted, changed, pure file move, move-and-change, case-only file move, moved directory, binary-like, spaces, Unicode, and semicolon paths. Confirm file moves remain delete/add patches, moved directories fail explicitly, and incomplete download batches produce no patch.
21. Submit changeset requests from two configured workspaces simultaneously. Confirm both complete, active UVCS process count remains one, and local comparisons can overlap UVCS work.
22. Stop the broker during an active batch. Confirm the child process is terminated, the request receives exit code `4`, and no partial queue result is consumed as success.

## Security Model

NV-AITools is intentionally installed in a directory writable by the current Windows user. Run Codex and other AI agents inside a sandbox that prevents writes outside approved workspaces. Without that boundary, an unrestricted AI can replace the executable and execute arbitrary code; using the tools with an unsandboxed AI means accepting that risk.

## Release

1. Optionally sign `NV-AITools.exe` with Authenticode to avoid unsigned-application warnings.
2. Generate a SHA-256 hash for the versioned ZIP.
3. Share the ZIP and its hash together.
