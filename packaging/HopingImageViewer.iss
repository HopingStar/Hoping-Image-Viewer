; Hoping Image Viewer — Inno Setup 安装脚本
; 支持选择安装路径；可选「绿色安装」；普通安装带卸载程序。
; 用法（在 packaging/ 目录）：
;   "C:\Users\<用户名>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" HopingImageViewer.iss
#define AppVer "1.0.1"

[Setup]
; 卸载注册表项的 AppId（代码里删除时也用同一值）
AppId={{F4A6C2E9-7B3D-4A5E-9C8B-2D1E6F5A3B7C}
AppName=Hoping Image Viewer
AppVersion={#AppVer}
AppPublisher=HopingStar
DefaultDirName={autopf}\Hoping Image Viewer
DefaultGroupName=Hoping Image Viewer
; 目录页保持 Inno 默认（auto）：若已安装过同 AppId，自动复用原安装目录覆盖安装，避免多份副本
DisableProgramGroupPage=no
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\ImageViewer.App\HopingImageViewer.ico
UninstallDisplayIcon={app}\HopingImageViewer.exe
UninstallDisplayName=Hoping Image Viewer
OutputDir=dist
OutputBaseFilename=HopingImageViewer-setup-{#AppVer}-win-x64

[Tasks]
Name: "green"; Description: "绿色安装（便携模式）：不建卸载程序/注册表，文件夹可整体拷贝带走"; GroupDescription: "安装模式:"; Flags: unchecked
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
Source: "staging\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; Excludes: "*.pdb"

[UninstallDelete]
; WebView2 运行时缓存（程序运行产生，不在安装清单里），卸载时进程已退出可安全删除
Type: filesandordirs; Name: "{app}\.webview2"
; 启动调试日志（程序运行产生）
Type: files; Name: "{app}\startup.log"

[Icons]
Name: "{group}\Hoping Image Viewer"; Filename: "{app}\HopingImageViewer.exe"; Tasks: not green
Name: "{group}\卸载 Hoping Image Viewer"; Filename: "{uninstallexe}"; Tasks: not green
Name: "{autodesktop}\Hoping Image Viewer"; Filename: "{app}\HopingImageViewer.exe"; Tasks: desktopicon and not green

[Code]
var
  DeleteData: Boolean;

function IsGreen: Boolean;
begin
  Result := WizardIsTaskSelected('green');
end;

{ 卸载确认：勾选方式决定是否删除数据；点「取消」中止卸载。
  勾选 = 删除 data；不勾选 = 保留数据到安装目录上级的 HopingImageViewer-data 文件夹。 }
function InitializeUninstall: Boolean;
var
  Form: TForm;
  LblText: TNewStaticText;
  HintText: TNewStaticText;
  Check: TNewCheckBox;
  CancelBtn, OkBtn: TNewButton;
begin
  DeleteData := False;
  Form := TForm.Create(nil);
  try
    Form.Caption := '卸载 Hoping Image Viewer';
    Form.ClientWidth := ScaleX(450);
    Form.ClientHeight := ScaleY(190);
    Form.Position := poScreenCenter;
    Form.BorderStyle := bsDialog;

    LblText := TNewStaticText.Create(Form);
    LblText.Parent := Form;
    LblText.Left := ScaleX(20);
    LblText.Top := ScaleY(16);
    LblText.Width := Form.ClientWidth - ScaleX(40);
    LblText.WordWrap := True;
    LblText.Caption := '确定要卸载 Hoping Image Viewer 吗？';

    HintText := TNewStaticText.Create(Form);
    HintText.Parent := Form;
    HintText.Left := ScaleX(20);
    HintText.Top := ScaleY(42);
    HintText.Width := Form.ClientWidth - ScaleX(40);
    HintText.WordWrap := True;
    HintText.Caption := '程序数据（相册链接 / 图片标签 / 排序设置 / AI 配置）保存在程序目录 data 文件夹，' + #13#10 +
      '不勾选将保留到安装目录上级的 HopingImageViewer-data 文件夹。';

    Check := TNewCheckBox.Create(Form);
    Check.Parent := Form;
    Check.Left := ScaleX(20);
    Check.Top := ScaleY(96);
    Check.Checked := False;
    Check.Caption := '同时删除程序数据';

    CancelBtn := TNewButton.Create(Form);
    CancelBtn.Parent := Form;
    CancelBtn.Caption := '取消';
    CancelBtn.ModalResult := mrCancel;
    CancelBtn.Top := Form.ClientHeight - ScaleY(58);
    CancelBtn.Left := Form.ClientWidth - ScaleX(118);
    CancelBtn.Width := ScaleX(90);

    OkBtn := TNewButton.Create(Form);
    OkBtn.Parent := Form;
    OkBtn.Caption := '卸载';
    OkBtn.ModalResult := mrOk;
    OkBtn.Default := True;
    OkBtn.Top := CancelBtn.Top;
    OkBtn.Left := CancelBtn.Left - ScaleX(98);
    OkBtn.Width := ScaleX(90);

    if Form.ShowModal = mrOk then
    begin
      DeleteData := Check.Checked;
      Result := True;    // 继续卸载
    end
    else
      Result := False;   // 取消卸载
  finally
    Form.Free;
  end;
end;

{ 卸载删除文件前，先结束正在运行的程序（含 WebView2 子进程树）。
  否则 exe / dll 全被进程锁定，Inno 删不掉，卸载后残留大量文件。 }
procedure KillAppProcess;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM HopingImageViewer.exe /T',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ 处理程序数据：删 data 或移到上级；再让 Inno 删除其余文件 }
procedure CurUninstallStepChanged(CurStep: TUninstallStep);
var
  DataPath: string;
  BackupPath: string;
  i: Integer;
begin
  if CurStep = usUninstall then
  begin
    KillAppProcess;  { 先杀进程，避免后续删除被锁定 }
    DataPath := ExpandConstant('{app}\data');
    if DeleteData then
    begin
      { 选了「删除数据」：data 不在安装清单里，需手动删 }
      if DirExists(DataPath) then
        DelTree(DataPath, True, True, True);
    end
    else if DirExists(DataPath) then
    begin
      { 选了「保留数据」：把 data 移出到安装目录上级 }
      BackupPath := ExpandConstant('{app}\..\HopingImageViewer-data');
      i := 1;
      while DirExists(BackupPath) do
      begin
        BackupPath := ExpandConstant('{app}\..\HopingImageViewer-data') + IntToStr(i);
        Inc(i);
      end;
      RenameFile(DataPath, BackupPath);
    end;
  end;
  if (CurStep = usPostUninstall) and (not DeleteData) then
  begin
    MsgBox('程序数据已保留在：' + #13#10 + ExpandConstant('{app}\..\HopingImageViewer-data'), mbInformation, MB_OK);
  end;
end;

{ 绿色安装：跳过「开始菜单文件夹」页（不建快捷方式） }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if IsGreen and (PageID = wpSelectProgramGroup) then
    Result := True;
end;

{ 绿色安装：安装完成后删除卸载程序与卸载注册表项，达到「绿色便携」效果 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Key: string;
begin
  if (CurStep = ssPostInstall) and IsGreen then
  begin
    DeleteFile(ExpandConstant('{app}\unins000.exe'));
    DeleteFile(ExpandConstant('{app}\unins000.dat'));
    Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{F4A6C2E9-7B3D-4A5E-9C8B-2D1E6F5A3B7C}_is1';
    RegDeleteKeyIncludingSubkeys(HKCU, Key);
    RegDeleteKeyIncludingSubkeys(HKLM, Key);
  end;
end;
