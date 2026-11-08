' Starts tools\Start-BuilderEngine.ps1 with no console window at all (the Start menu shortcut runs this through wscript.exe).
' "powershell -WindowStyle Hidden" alone still flashes a console for a moment; window style 0 here never shows one.
Dim fso, here, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & here & "\Start-BuilderEngine.ps1"""
CreateObject("WScript.Shell").Run cmd, 0, False
