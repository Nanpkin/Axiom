#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif

[Setup]
AppId={{48771464-8F75-46D2-A3CF-8704B8BECCF7}
AppName=Axiom
AppVersion={#AppVersion}
AppPublisher=Axiom
DefaultDirName={localappdata}\Programs\Axiom
DefaultGroupName=Axiom
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#ReleaseDir}
OutputBaseFilename=Axiom-{#AppVersion}-win-x64-setup
SetupIconFile=..\assets\Axiom.ico
UninstallDisplayIcon={app}\Axiom.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
Uninstallable=yes
UninstallDisplayName=Axiom

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PackageDir}\Axiom.exe"; Flags: dontcopy
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Axiom"; Filename: "{app}\Axiom.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Axiom"; Filename: "{app}\Axiom.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Axiom.exe"; Description: "打开 Axiom"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  ExtractTemporaryFile('Axiom.exe');
  if not Exec(ExpandConstant('{tmp}\Axiom.exe'), '--check-stopped', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := '无法确认 Axiom 是否已停止，安装已取消。'
  else if ResultCode <> 0 then
    Result := '请先在 Axiom 设置中选择“停止后台并退出”，再继续安装。正在控制游戏时请先退出游戏并等待恢复。';
end;

function InitializeUninstall(): Boolean;
var ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{app}\Axiom.exe'), '--prepare-uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    MsgBox('请先在 Axiom 设置中关闭登录启动并选择“停止后台并退出”，再卸载。不会强制终止后台或删除个人配置。', mbError, MB_OK);
end;
