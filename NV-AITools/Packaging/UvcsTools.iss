#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{9D08F3BC-53D2-4F9B-9418-7DA7C35D4F33}
AppName=UvcsTools
AppVersion={#AppVersion}
DefaultDirName={autopf}\UvcsTools
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir=..\Artifacts\installer
OutputBaseFilename=UvcsTools-Setup-{#AppVersion}
UninstallDisplayIcon={app}\UvcsTools.exe

[Tasks]
Name: "installskill"; Description: "Install the UvcsTools Codex skill for the current Windows user"

[Files]
Source: "..\Artifacts\publish\win-x64\UvcsTools.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\.codex\skills\use-uvcs-tools\*"; DestDir: "{app}\SkillTemplate\use-uvcs-tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "InstallSkill.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Artifacts\UvcsTools-WorkspaceKit-{#AppVersion}.zip"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{app}\InstallSkill.bat"; Parameters: "install"; StatusMsg: "Installing the Codex skill for the current user..."; Flags: runasoriginaluser waituntilterminated; Tasks: installskill
