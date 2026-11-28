#define AppExe "LiuLianToolBox.exe"
#define Payload AddBackslash(SourcePath) + "..\artifacts\win-x64\" + AppExe
#define FileVersion GetVersionNumbersString(Payload)

[Setup]
AppId=LiuLianMagicBox.LiuLianToolBox
AppName=LiuLianToolBox
AppVersion={#RemoveFileExt(FileVersion)}
VersionInfoVersion={#FileVersion}
AppPublisher=LiuLianMagicBox
AppPublisherURL=https://www.liulian.dev/
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={userpf}\LiuLianToolBox
DisableDirPage=yes
DisableProgramGroupPage=yes
AppMutex=Local\LiuLianToolBox.Desktop
SetupIconFile=..\src\ToolBox.Desktop\Assets\ToolBox.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
OutputDir=..\artifacts\installer
OutputBaseFilename=LiuLianToolBox-Setup-x64

[Languages]
Name: "zhTW"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
Name: "zhCN"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
zhTW.SetupAppRunningError=%1 正在執行。請在通知區圖示按右鍵，選「結束 ToolBox」後按「確定」。使用 0.2 版時可直接關閉主視窗。
zhCN.SetupAppRunningError=%1 正在运行。请右键点击通知区域的图标，选择“退出 ToolBox”，然后点击“确定”。使用 0.2 版时可直接关闭主窗口。
en.SetupAppRunningError=%1 is running. Right-click its notification area icon, select Exit ToolBox, then click OK. If you are using version 0.2, close its main window.
zhTW.UninstallAppRunningError=%1 正在執行。請在通知區圖示按右鍵，選「結束 ToolBox」後按「確定」以繼續移除。
zhCN.UninstallAppRunningError=%1 正在运行。请右键点击通知区域的图标，选择“退出 ToolBox”，然后点击“确定”继续卸载。
en.UninstallAppRunningError=%1 is running. Right-click its notification area icon, select Exit ToolBox, then click OK to continue uninstalling.

[CustomMessages]
zhTW.StartAtSignIn=登入 Windows 時在背景啟動 LiuLianToolBox
zhCN.StartAtSignIn=登录 Windows 时在后台启动 LiuLianToolBox
en.StartAtSignIn=Start LiuLianToolBox in the background when I sign in to Windows

[Tasks]
Name: "startup"; Description: "{cm:StartAtSignIn}"; Flags: unchecked

[Files]
Source: "{#Payload}"; DestDir: "{app}"

[Icons]
Name: "{userprograms}\LiuLianToolBox"; Filename: "{app}\{#AppExe}"
Name: "{userstartup}\LiuLianToolBox"; Filename: "{app}\{#AppExe}"; Parameters: "--background"; Tasks: startup

[InstallDelete]
Type: files; Name: "{userstartup}\LiuLianToolBox.lnk"; Tasks: not startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,LiuLianToolBox}"; Flags: nowait postinstall skipifsilent
