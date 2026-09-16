Option Explicit
Dim shell, files, folder, logPath, command, result, details
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
folder = files.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = folder
logPath = files.BuildPath(files.GetSpecialFolder(2), files.GetTempName)
command = Quote(shell.ExpandEnvironmentStrings("%ComSpec%")) & " /d /c " & Quote(Quote(files.BuildPath(folder, "build.cmd")) & " > " & Quote(logPath) & " 2>&1")
result = shell.Run(command, 0, True)
If result <> 0 Then
    details = "Could not build Screen Zoom."
    If files.FileExists(logPath) Then details = files.OpenTextFile(logPath, 1).ReadAll
    MsgBox details, vbCritical, "Screen Zoom"
Else
    shell.Run Quote(files.BuildPath(folder, "bin\ScreenZoom.exe")), 1, False
End If
If files.FileExists(logPath) Then files.DeleteFile logPath

Function Quote(value)
    Quote = Chr(34) & value & Chr(34)
End Function
