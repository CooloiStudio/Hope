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

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "开机自动启动 Hope"; GroupDescription: "启动选项:"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Hope"; Filename: "{app}\{#DesktopExe}"
Name: "{group}\卸载 Hope"; Filename: "{uninstallexe}"

[Registry]
; 开机自启（HKCU，免管理员）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "Hope"; ValueData: """{app}\{#DesktopExe}"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#DesktopExe}"; Description: "立即启动 Hope"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前结束进程，确保文件可删除
Filename: "{cmd}"; Parameters: "/C taskkill /IM hope-desktop.exe /IM hope-headless.exe /F"; Flags: runhidden; RunOnceId: "KillHope"

[UninstallDelete]
; 清理用户数据（文档 §7.7：完全清理 %APPDATA%\Hope）
Type: filesandordirs; Name: "{userappdata}\Hope"
