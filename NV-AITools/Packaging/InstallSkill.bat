@echo off
setlocal EnableExtensions

set "ACTION=%~1"
set "SOURCE=%~dp0SkillTemplate\use-uvcs-tools"
set "TARGET=%USERPROFILE%\.agents\skills\use-uvcs-tools"

if not exist "%SOURCE%\SKILL.md" set "SOURCE=%~dp0..\.codex\skills\use-uvcs-tools"

if /i "%ACTION%"=="remove" goto Remove
if not "%ACTION%"=="" if /i not "%ACTION%"=="install" (
    echo Usage: InstallSkill.bat [install^|remove]
    exit /b 2
)

if not exist "%SOURCE%\SKILL.md" (
    echo ERROR: Skill template is missing: "%SOURCE%\SKILL.md"
    exit /b 1
)

if not exist "%TARGET%" mkdir "%TARGET%"
if errorlevel 1 (
    echo ERROR: Could not create "%TARGET%".
    exit /b 1
)

xcopy "%SOURCE%\*" "%TARGET%\" /E /I /Q /Y >nul
if errorlevel 1 (
    echo ERROR: Could not install the UvcsTools skill to "%TARGET%".
    exit /b 1
)

echo Installed UvcsTools skill to "%TARGET%".
exit /b 0

:Remove
if exist "%TARGET%" rmdir /s /q "%TARGET%"
if exist "%TARGET%" (
    echo ERROR: Could not remove "%TARGET%".
    exit /b 1
)
echo Removed UvcsTools skill from "%TARGET%".
exit /b 0
