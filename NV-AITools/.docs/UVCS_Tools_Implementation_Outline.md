# UVCS Read-Only Tools

*Concise implementation outline for a single C# console executable with human-friendly batch wrappers.*

## 1. Purpose and Scope

Replace the current PowerShell-heavy workflows with one conventional compiled C# console application. The application exposes a small set of read-only Unity Version Control operations selected by command-line input.

- Humans run batch wrappers that save clean output files.
- AI agents invoke the executable directly and receive the result through standard output.

## 2. Core Design Decisions

| Area | Decision |
|---|---|
| **Executable** | One central executable, tentatively `UvcsTools.exe`, branching internally by subcommand. |
| **Compilation** | Normal .NET build only. No Native AOT and no Python packaging. |
| **Safety** | Only fixed read-only workflows are implemented. No arbitrary `cm` command or argument passthrough. |
| **Agent output** | Final result only on stdout; progress and diagnostics on stderr; nonzero exit code on failure. |
| **Human output** | Small `.bat` wrappers prompt or accept parameters, redirect stdout into named files, display the saved path, and pause. |
| **Files** | The executable does not accept a normal final-output-file parameter. Internal temporary files are allowed and deleted after use. |
| **Trust boundary** | Install the compiled release under Program Files or another directory the agent can execute but not modify. NTFS permissions provide the protection, not compilation alone. |

## 3. Public Command Interface

```text
UvcsTools.exe status [--workspace <path>]
UvcsTools.exe pending-changes-diffs [--workspace <path>]
UvcsTools.exe changeset-diffs --from <changeset> --to <changeset>
    [--workspace <path>]
    [--algorithm histogram|patience|default|minimal]
    [--find-renames]
```

- The workspace defaults to the current directory and is resolved to the actual workspace root through `cm getworkspacefrompath`.
- Unknown commands, duplicate options, missing values, invalid changeset numbers, or unsupported algorithms fail before any Plastic command runs.
- No `--cm-args`, `--command`, shell string, or similar generic escape hatch is provided.

## 4. Tool Behaviors

### 4.1 Status

- Run the UVCS status command using machine-readable output.
- Parse the result rather than forwarding raw Plastic formatting.
- Write clean structured output to stdout, preferably JSON with:
  - Schema version
  - Workspace root
  - Status entries
  - Status counts
- Do not create or modify any project files.

### 4.2 PendingChangesDiffs

Preserve the effective behavior of `1PendingChangesDiff.bat`, while streaming the completed report to stdout instead of writing the final report itself:

- Verify `cm` is available and resolve the workspace root.
- Run `cm workspacestatus` in machine-readable mode for changed, added, deleted, local-deleted, moved, local-moved, checkout, and private entries.
- Include only `.cs`, `.shader`, `.cginc`, and `.compute` files.
- Ignore checkout-only entries.
- Sort matching paths and emit a section for each file containing:
  - Relative path
  - Status label/code
  - Full path
- Skip moved and local-moved entries, matching the current script.
- For controlled files:
  - Obtain `RevisionChangeset` with `cm fileinfo`.
  - Fetch the baseline with `cm getfile`.
- Use an empty baseline for added or private files.
- Use the working file as the new side, or an empty file when the working file is absent or deleted.
- Compare the two temporary files with `fc.exe /n` and include its numbered text result.
- Finish with processed, skipped, and failed counts.
- Delete all temporary files.

### 4.3 ChangesetDiffs

Preserve the effective behavior of `PlasticDiffGenerator.ps1` and `RunPlasticDiffGen.bat`, while streaming the final patch to stdout:

- Require integer `--from` and `--to` changesets.
- Use the current extension allowlist:
  - `.cs`
  - `.asmdef`
  - `.asmref`
  - `.shader`
  - `.hlsl`
  - `.compute`
  - `.cginc`
  - `.uxml`
  - `.uss`
- Run `cm log` from the starting changeset through the ending changeset and request repository paths.
- Parse short status plus path, filter by extension, and deduplicate paths.
- Export each candidate at both endpoint changesets with `cm cat` into temporary old and new trees.
- Interpret endpoint state as follows:
  - Missing old endpoint: added
  - Missing new endpoint: deleted
  - Both endpoints present: changed
  - Both endpoints missing: no endpoint diff
- Scan exported files for NUL bytes and skip binary-like content.
- Run `git diff --no-index` with:
  - Line-ending conversion disabled
  - External diff and text conversion disabled
  - Histogram algorithm by default
  - Rename detection disabled by default
- Return the unified patch through stdout.
- Treat Git exit codes as follows:
  - `0`: no endpoint differences
  - `1`: differences found; successful result
  - Greater than `1`: error
- Delete the temporary endpoint trees after completion.

## 5. Process and Output Contract

| Channel | Contract |
|---|---|
| **stdout** | Only the final status JSON, pending-change report, or changeset patch. This is what the agent consumes and what batch files redirect. |
| **stderr** | Progress, warnings, dependency checks, and actionable error messages. Never mix diagnostics into the diff or JSON payload. |
| **Exit 0** | Successful command, including a valid empty status or result when appropriate. |
| **Exit 2** | Invalid command-line input. |
| **Exit 3** | Workspace or dependency problem, such as `cm` or Git being unavailable. |
| **Exit 4** | External command failure. |
| **Exit 5** | Parsing, temporary-file, or internal processing failure. |

## 6. Human-Run Batch Wrappers

Suggested project layout:

```text
Review/
  RunUvcsStatus.bat
  RunPendingChangesDiffs.bat
  RunChangesetDiffs.bat
  Reviews/
```

- Each wrapper locates the workspace relative to its own `Review` folder.
- Each wrapper invokes the installed executable by absolute path.
- Suggested output naming:
  - Status: timestamped `.json`
  - Pending changes: timestamped `.diff.txt`
  - Changeset differences: `plastic-log-diff-FROM-to-TO.patch`
- Wrappers may prompt when required arguments are absent.
- Wrappers should also accept command-line arguments for quick repeatable use.
- Redirect stdout to a temporary file first.
- Only rename or move the temporary file to the final `Reviews` filename when:
  - The executable exits successfully.
  - The output passes a basic nonempty or validity check.
- Leave stderr visible in the console.
- Report success or failure, print the final path, and pause for human use.

Example:

```bat
"C:\Program Files\UvcsTools\UvcsTools.exe" changeset-diffs --from 20640 --to 20663 > "%TEMP_PATCH%"
if errorlevel 1 goto Failed
move /y "%TEMP_PATCH%" "%REVIEWS_DIR%\plastic-log-diff-20640-to-20663.patch"
```

## 7. AI-Agent Usage

```powershell
& "C:\Program Files\UvcsTools\UvcsTools.exe" status
& "C:\Program Files\UvcsTools\UvcsTools.exe" pending-changes-diffs
& "C:\Program Files\UvcsTools\UvcsTools.exe" changeset-diffs --from 20640 --to 20663
```

- The agent invokes the executable directly, not the batch wrappers.
- The agent reads:
  - stdout as the tool result
  - stderr as diagnostics
  - The process exit code as success or failure
- `AGENTS.md` or a Codex skill should document the exact approved commands.
- The agent should be instructed not to invoke `cm.exe` directly.

## 8. Suggested C# Structure

```text
UvcsTools/
  Program.cs
  Commands/
    StatusCommand.cs
    PendingChangesDiffsCommand.cs
    ChangesetDiffsCommand.cs
  Infrastructure/
    CmRunner.cs
    GitRunner.cs
    ProcessResult.cs
    WorkspaceResolver.cs
    TempDirectory.cs
  Models/
    StatusEntry.cs
    StatusResult.cs
  Formatting/
    StatusJsonWriter.cs
    PendingDiffWriter.cs
```

Responsibilities:

- `Program.cs`: command dispatch, argument validation, and the top-level exit-code boundary.
- Use `ProcessStartInfo` with `UseShellExecute = false` and `ArgumentList`.
- Never construct shell command strings.
- Centralize:
  - Timeouts
  - Output-size limits
  - Process cleanup
  - UTF-8 handling
  - External-command error reporting
- Keep the three command implementations independent.
- Share only low-level process and workspace utilities.

## 9. Initial Acceptance Criteria

1. All three commands run from an arbitrary subdirectory inside a valid UVCS workspace.
2. Direct executable invocation creates no final output file and produces a clean stdout payload.
3. Batch wrappers produce correctly named files under `Review\Reviews` without contaminating them with progress messages.
4. `PendingChangesDiffs` matches the current script's filtering, baseline retrieval, move skipping, `fc.exe` output, and summary behavior.
5. `ChangesetDiffs` matches the current extension filtering, endpoint export, binary detection, Git diff options, and add/delete semantics.
6. Invalid input cannot cause arbitrary `cm`, Git, PowerShell, `cmd`, or shell commands to execute.
7. Temporary content is removed on success and failure.
