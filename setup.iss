; Hope（盼头）安装包 — Inno Setup 脚本（Phase 1）
; 用法：iscc /DStageDir=<编译产物目录> /DOutputDir=<输出目录> setup.iss
; StageDir 应包含 hope-headless.exe、hope-desktop.exe 及其依赖。

#ifndef StageDir
  #define StageDir "stage"
#endif
#ifndef OutputDir
  #define OutputDir "dist"
#endif
; AppVersion 由 CI 通过 /DAppVersion=<桌面端版本> 注入；本地直接 iscc 时回退到占位值。
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "Hope"
#define AppPublisher "Hope"
#define DesktopExe "hope-desktop.exe"

[Setup]
AppId={{B7E4F2A1-9C3D-4E5F-8A6B-1D2C3E4F5A6B}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; AppMutex 与桌面端单实例互斥量同名：静默升级（/CLOSEAPPLICATIONS）时安装器能检测并关闭运行中的实例。
AppMutex=Global\HopeDesktop
CloseApplications=yes
RestartApplications=no
DefaultDirName={autopf}\Hope
DefaultGroupName=Hope
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Hope_Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
; 按系统 UI 语言自动选择；多语言时仍可在向导首屏切换。
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.CreateDesktopIcon=Create a desktop shortcut
english.CreateStartMenuIcon=Add to Start Menu
english.AdditionalIcons=Shortcuts:
english.AutoStart=Start Hope automatically at logon
english.StartupOptions=Startup options:
english.LaunchHope=Launch Hope now
english.UninstallHope=Uninstall Hope

chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.CreateStartMenuIcon=添加到开始菜单
chinesesimplified.AdditionalIcons=快捷方式:
chinesesimplified.AutoStart=开机自动启动 Hope
chinesesimplified.StartupOptions=启动选项:
chinesesimplified.LaunchHope=立即启动 Hope
chinesesimplified.UninstallHope=卸载 Hope

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "{cm:CreateStartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:StartupOptions}"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
; 开始菜单（可选，默认勾选）
Name: "{group}\Hope"; Filename: "{app}\{#DesktopExe}"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallHope}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
; 桌面快捷方式（可选，默认勾选）
Name: "{autodesktop}\Hope"; Filename: "{app}\{#DesktopExe}"; Tasks: desktopicon

[Registry]
; 开机自启（HKCU，免管理员）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "Hope"; ValueData: """{app}\{#DesktopExe}"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#DesktopExe}"; Description: "{cm:LaunchHope}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前结束进程，确保文件可删除
Filename: "{cmd}"; Parameters: "/C taskkill /IM hope-desktop.exe /IM hope-headless.exe /F"; Flags: runhidden; RunOnceId: "KillHope"

[UninstallDelete]
; 清理用户数据（文档 §7.7：完全清理 %APPDATA%\Hope）
Type: filesandordirs; Name: "{userappdata}\Hope"
