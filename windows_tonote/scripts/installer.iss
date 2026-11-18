; W-23：同笔记 Windows 安装包（Inno Setup 6）
; 用法：先运行 scripts/publish-windows.sh，再用 Inno Setup 编译本脚本。
#define MyAppName "同笔记"
#define MyAppVersion "1.0.0"
#define MyAppExeName "Tonote.exe"

[Setup]
AppId={{9A2E4C70-6B1D-4E3F-9D17-5A2C8B0E7F31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\TongNote
DefaultGroupName={#MyAppName}
OutputBaseFilename=TongNoteSetup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "dist/win-x64/*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"

[Registry]
; 安装期注册 tongnote://，不依赖首次运行时写 HKCU
Root: HKCU; Subkey: "Software\Classes\tongnote"; ValueType: string; ValueName: ""; ValueData: "URL:TongNote Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\tongnote"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\tongnote\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey

[Run]
; WebView2 Evergreen Bootstrapper 静默安装；机器上已有时会快速退出
Filename: "{app}\WebView2Bootstrapper.exe"; Parameters: "/silent /norestart"; Flags: skipifdoesntexist nowait
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动同笔记"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 默认保留用户数据（%APPDATA%\Tonote 与网盘内容），只删程序自身文件
Type: filesandordirs; Name: "{app}\webview2"
