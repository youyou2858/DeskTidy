Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
exe = "D:\WorkBuddy AI\2026-10-22-23-28-24\DeskTidy\publish\DeskTidy.exe"
desktop = sh.SpecialFolders("Desktop")
Set lnk = sh.CreateShortcut(desktop & "\DeskTidy.lnk")
lnk.TargetPath = exe
lnk.WorkingDirectory = fso.GetParentFolderName(exe)
lnk.IconLocation = exe & ",0"
lnk.Description = "DeskTidy 桌面分区整理"
lnk.Save
WScript.Echo "created: " & desktop & "\DeskTidy.lnk"
