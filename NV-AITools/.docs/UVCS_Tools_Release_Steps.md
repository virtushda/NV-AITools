# UVCS Tools Release Steps

## Prerequisites

1. Install the .NET 10 SDK.
2. Download and install [Inno Setup](https://jrsoftware.org/isdl.php). `ISCC.exe` is included and is detected automatically by the build script.
3. Keep the application version in `major.minor.patch` form, such as `1.0.0`.

Inno Setup can also be installed interactively with Windows Package Manager:

```bat
winget install --id JRSoftware.InnoSetup -e -s winget -i
```

## Build

Run [BuildRelease.bat](../Packaging/BuildRelease.bat) from a normal command prompt:

```bat
Packaging\BuildRelease.bat 1.0.0
```

The script restores the project, publishes a self-contained single-file Windows x64 executable, creates the workspace-kit ZIP, and compiles [UvcsTools.iss](../Packaging/UvcsTools.iss).

Use publish-only mode when Inno Setup is not installed:

```bat
Packaging\BuildRelease.bat 1.0.0 --publish-only
```

## Verify

1. Launch the generated setup executable normally and accept its UAC prompt. Do not use **Run as administrator**, because Windows would prevent the installer from returning to the original user identity for skill installation.
2. Confirm the installed application directory is writable only by administrators and readable/executable by ordinary users.
3. Confirm the skill exists under `%USERPROFILE%\.agents\skills\use-uvcs-tools`.
4. Extract the workspace-kit ZIP into a real UVCS workspace root and confirm these files exist:
   - [RunUvcsStatus.bat](../Review/RunUvcsStatus.bat)
   - [RunPendingChangesDiffs.bat](../Review/RunPendingChangesDiffs.bat)
   - [RunChangesetDiffs.bat](../Review/RunChangesetDiffs.bat)
5. Run all three wrappers and verify their outputs.
6. Test upgrade and uninstall behavior.
7. The machine-wide uninstaller intentionally leaves the per-user skill in place. Run [InstallSkill.bat](../Packaging/InstallSkill.bat) with `remove` before uninstalling when the skill should also be removed.

## Release

1. Sign the application and setup executable with Authenticode and timestamp both signatures.
2. Generate SHA-256 hashes for the setup executable and workspace-kit ZIP.
3. Publish the two versioned artifacts and their hashes together.
4. Keep the `AppId` in [UvcsTools.iss](../Packaging/UvcsTools.iss) unchanged across releases.
