#ifndef PayloadDir
  #error PayloadDir must point to the generated distribution payload.
#endif
#ifndef AppVersion
  #error AppVersion must match the MSIX package version.
#endif
#ifndef CertificateThumbprint
  #error CertificateThumbprint is required.
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId=AShareWidgetInstaller
AppName=股票行情小组件
AppVersion={#AppVersion}
AppPublisher=AShareWidget
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
MinVersion=10.0.22000
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir={#OutputDir}
OutputBaseFilename=AShareWidget-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PayloadDir}\AShareWidget.msix"; Flags: dontcopy
Source: "{#PayloadDir}\AShareWidget.cer"; Flags: dontcopy
Source: "{#PayloadDir}\WindowsAppRuntimeInstall-x64.exe"; Flags: dontcopy
Source: "{#PayloadDir}\release.json"; Flags: dontcopy
Source: "{#PayloadDir}\Install.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\Trust-Certificate.ps1"; Flags: dontcopy

[Code]
var
  CertificatePage: TWizardPage;
  CertificateButton: TNewButton;
  CertificateStatus: TNewStaticText;
  PayloadExtracted: Boolean;

function CmdLineParamExists(Value: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), Value) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function IsCertificateTrusted: Boolean;
begin
  Result := RegKeyExists(HKLM64,
    'SOFTWARE\Microsoft\SystemCertificates\TrustedPeople\Certificates\{#CertificateThumbprint}');
end;

procedure ExtractPayload;
begin
  if PayloadExtracted then Exit;
  ExtractTemporaryFile('AShareWidget.msix');
  ExtractTemporaryFile('AShareWidget.cer');
  ExtractTemporaryFile('WindowsAppRuntimeInstall-x64.exe');
  ExtractTemporaryFile('release.json');
  ExtractTemporaryFile('Install.ps1');
  ExtractTemporaryFile('Trust-Certificate.ps1');
  PayloadExtracted := True;
end;

procedure InstallCertificateClick(Sender: TObject);
var
  ExitCode: Integer;
  Parameters: String;
begin
  CertificateButton.Enabled := False;
  try
    ExtractPayload;
    Parameters := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\Install.ps1') + '" -CertificateOnly';
    if Exec(ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe'),
      Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0) and IsCertificateTrusted then
    begin
      CertificateStatus.Caption := '证书已安装，可以继续安装应用。';
      CertificateButton.Caption := '证书已安装';
    end
    else
    begin
      CertificateStatus.Caption := '证书未安装，请点击按钮并允许 Windows 管理员授权。';
      CertificateButton.Enabled := True;
    end;
  except
    CertificateStatus.Caption := GetExceptionMessage;
    CertificateButton.Enabled := True;
  end;
  WizardForm.NextButton.Enabled := IsCertificateTrusted;
end;

procedure InitializeWizard;
begin
  CertificatePage := CreateCustomPage(wpWelcome, '安装证书', '首次安装需要信任此应用的开发者证书。');
  CertificateStatus := TNewStaticText.Create(CertificatePage);
  CertificateStatus.Parent := CertificatePage.Surface;
  CertificateStatus.Caption := '点击“安装证书”，并允许 Windows 弹出的管理员授权。';
  CertificateStatus.Top := ScaleY(12);
  CertificateStatus.Width := CertificatePage.SurfaceWidth;
  CertificateStatus.Height := ScaleY(48);
  CertificateStatus.WordWrap := True;
  CertificateButton := TNewButton.Create(CertificatePage);
  CertificateButton.Parent := CertificatePage.Surface;
  CertificateButton.Caption := '安装证书';
  CertificateButton.Top := ScaleY(76);
  CertificateButton.Width := ScaleX(140);
  CertificateButton.Height := ScaleY(32);
  CertificateButton.OnClick := @InstallCertificateClick;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = CertificatePage.ID) and
    (IsCertificateTrusted or CmdLineParamExists('/VALIDATEONLY'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Parameters: String;
  ExitCode: Integer;
begin
  Result := '';
  try
    WizardForm.PreparingLabel.Caption := '正在检查依赖并安装股票行情小组件，请稍候…';
    ExtractPayload;
    Parameters := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\Install.ps1') + '"';
    if CmdLineParamExists('/VALIDATEONLY') then
      Parameters := Parameters + ' -ValidateOnly';
    if not Exec(ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe'),
      Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Result := '无法启动 Windows PowerShell。'
    else if ExitCode <> 0 then
      Result := '安装未完成。请检查管理员授权或系统策略。详细日志：' +
        ExpandConstant('{%TEMP}\AShareWidget-install.log');
  except
    Result := GetExceptionMessage;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = CertificatePage.ID then
    WizardForm.NextButton.Enabled := IsCertificateTrusted;
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := '安装完成。按 Win + W，在看板的“+”中添加“股票行情”。' + #13#10 +
      '覆盖更新会保留已固定的小组件和自选股配置。';
end;
