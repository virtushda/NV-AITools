Option Explicit

Dim executable, files, shell
Set files = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

executable = files.BuildPath(files.GetParentFolderName(WScript.ScriptFullName), "NV-AITools.exe")
shell.Run """" & executable & """ broker-start", 0, False
